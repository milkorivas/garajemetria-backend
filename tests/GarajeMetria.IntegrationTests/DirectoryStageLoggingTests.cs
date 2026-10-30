using System.Reflection;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Infrastructure.Authentication;
using Microsoft.Extensions.Logging;
using Novell.Directory.Ldap;

namespace GarajeMetria.IntegrationTests;

public sealed class DirectoryStageLoggingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserSearchStaysInTheDomainWithoutChangingBaseReads(bool refresh)
    {
        using var factory = LoggerFactory.Create(_ => { });
        var script = new DirectoryScript(refresh: refresh);
        var directory = new LdapCorporateDirectory(Options(), factory.CreateLogger<LdapCorporateDirectory>(), script.CreateConnection);
        var result = refresh ? await directory.ReadAsync(Guid.NewGuid(), default)
            : await directory.AuthenticateAsync("private-user", "private-password", default);

        Assert.Equal(DirectoryStatus.Valid, result.Status);
        Assert.NotEmpty(script.SearchConstraints);
        var control = Assert.Single(script.SearchConstraints[0].GetControls());
        Assert.Equal("1.2.840.113556.1.4.1339", control.Id);
        Assert.True(control.Critical);
        Assert.All(script.SearchConstraints, constraints => Assert.False(constraints.ReferralFollowing));
        Assert.All(script.SearchConstraints.Skip(1), constraints => Assert.True(constraints.GetControls() is null or { Length: 0 }));
    }

    [Theory]
    [InlineData("UserSearch", null, false)]
    [InlineData("UserSearch", null, true)]
    [InlineData("UserConnect", null, false)]
    [InlineData("UserBind", null, false)]
    [InlineData("UserRead", null, false)]
    [InlineData("GroupLookup", "Administrator", false)]
    [InlineData("GroupMembership", "Administrator", false)]
    [InlineData("GroupLookup", "Advisor", false)]
    [InlineData("GroupMembership", "Advisor", true)]
    public async Task ReferralFailureIdentifiesItsExactStageWithoutLeakingDirectoryData(string stage, string? groupRole, bool failOnNext)
    {
        var logs = new RecordingLogProvider();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        var script = new DirectoryScript(stage, groupRole, failOnNext);
        var directory = new LdapCorporateDirectory(Options(), factory.CreateLogger<LdapCorporateDirectory>(), script.CreateConnection);
        var result = await directory.AuthenticateAsync("private-user", "private-password", default);

        Assert.Equal(DirectoryStatus.Unavailable, result.Status);
        var failure = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal("ActiveDirectory." + stage, failure.Properties["Operation"]);
        Assert.Equal(groupRole, failure.Properties["GroupRole"]);
        Assert.Equal("referral_received", failure.Properties["Reason"]);
        Assert.Equal(10, failure.Properties["ErrorCode"]);
        Assert.All(logs.Entries, e => Assert.DoesNotContain("private-", e.Message));
        Assert.Null(failure.Exception);
    }

    [Fact]
    public async Task RejectedUserCredentialsAreLoggedAtUserBindWithoutStartingGroupChecks()
    {
        var logs = new RecordingLogProvider();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        var script = new DirectoryScript("UserBind") { CredentialRejection = true };
        var directory = new LdapCorporateDirectory(Options(), factory.CreateLogger<LdapCorporateDirectory>(), script.CreateConnection);
        var result = await directory.AuthenticateAsync("private-user", "private-password", default);

        Assert.Equal(DirectoryStatus.Rejected, result.Status);
        var failure = Assert.Single(logs.Entries, e => e.Level == LogLevel.Information);
        Assert.Equal("ActiveDirectory.UserBind", failure.Properties["Operation"]);
        Assert.Contains("cuenta de usuario", failure.Properties["Description"]!.ToString());
        Assert.DoesNotContain(logs.Entries, e => e.Properties["Operation"]!.ToString()!.Contains("Group", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuccessfulLoginRecordsOrderedStagesAtDebugAndClearsTheGroupContext()
    {
        var logs = new RecordingLogProvider();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        var script = new DirectoryScript();
        var directory = new LdapCorporateDirectory(Options(), factory.CreateLogger<LdapCorporateDirectory>(), script.CreateConnection);
        var result = await directory.AuthenticateAsync("private-user", "private-password", default);

        Assert.Equal(DirectoryStatus.Valid, result.Status);
        Assert.True(result.Identity!.IsAdministrator);
        Assert.False(result.Identity.IsAdvisor);
        Assert.Equal(new[] { "Connect", "ServiceBind", "UserSearch", "UserConnect", "UserBind", "UserRead", "UserAttributes",
                "GroupLookup", "GroupMembership", "GroupLookup", "GroupMembership", "UserProfile" },
            logs.Entries.Select(e => e.Properties["Operation"]!.ToString()!["ActiveDirectory.".Length..]));
        Assert.All(logs.Entries, e => Assert.Equal(LogLevel.Debug, e.Level));
        Assert.Null(logs.Entries.Last().Properties["GroupRole"]);
        Assert.All(logs.Entries, e => Assert.DoesNotContain("private-", e.Message));
    }

    [Fact]
    public async Task SessionRereadAlsoIdentifiesTheGroupThatFailed()
    {
        var logs = new RecordingLogProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var script = new DirectoryScript("GroupMembership", "Advisor", refresh: true);
        var directory = new LdapCorporateDirectory(Options(), factory.CreateLogger<LdapCorporateDirectory>(), script.CreateConnection);
        Assert.Equal(DirectoryStatus.Unavailable, (await directory.ReadAsync(Guid.NewGuid(), default)).Status);
        var failure = Assert.Single(logs.Entries);
        Assert.Equal("ActiveDirectory.GroupMembership", failure.Properties["Operation"]);
        Assert.Equal("Advisor", failure.Properties["GroupRole"]);
    }

    private static DirectoryOptions Options() => new()
    {
        Servers = ["private-server"],
        Port = 636,
        TimeoutSeconds = 5,
        SearchBase = "DC=private-domain",
        ServiceBindDn = "CN=private-service",
        ServicePassword = "private-service-password",
        AdministratorGroupDn = "CN=private-admin",
        AdvisorGroupDn = "CN=private-advisor"
    };

    // Test only the SDK boundary; production continues to create certificate-validating LDAPS connections.
    private sealed class DirectoryScript
    {
        private readonly string? failStage;
        private readonly string? failGroup;
        private readonly bool failOnNext;
        private readonly Queue<(string Stage, string? Group, LdapEntry? Entry)> searches = new();
        private int connections;
        public bool CredentialRejection { get; init; }
        public List<LdapSearchConstraints> SearchConstraints { get; } = [];

        public DirectoryScript(string? failStage = null, string? failGroup = null, bool failOnNext = false, bool refresh = false)
        {
            this.failStage = failStage;
            this.failGroup = failGroup;
            this.failOnNext = failOnNext;
            var attributes = new LdapAttributeSet();
            attributes.Add(new LdapAttribute("objectGUID", Guid.NewGuid().ToByteArray()));
            foreach (var (key, value) in new[] { ("userAccountControl", "512"), ("msDS-User-Account-Control-Computed", "0"),
                         ("pwdLastSet", "1"), ("sAMAccountName", "private-user"), ("displayName", "private-display") })
                attributes.Add(new LdapAttribute(key, value));
            var user = new LdapEntry("CN=private-user", attributes);
            var group = new LdapEntry("CN=private-group", new LdapAttributeSet());
            searches.Enqueue(("UserSearch", null, user));
            if (!refresh) searches.Enqueue(("UserRead", null, user));
            searches.Enqueue(("GroupLookup", "Administrator", group));
            searches.Enqueue(("GroupMembership", "Administrator", user));
            searches.Enqueue(("GroupLookup", "Advisor", group));
            searches.Enqueue(("GroupMembership", "Advisor", null));
        }

        public ILdapConnection CreateConnection()
        {
            var userConnection = connections++ > 0;
            var connection = Proxy<ILdapConnection>(method => method.Name switch
            {
                "ConnectAsync" => ConnectionStep(userConnection ? "UserConnect" : "Connect"),
                "BindAsync" => ConnectionStep(userConnection ? "UserBind" : "ServiceBind"),
                "SearchAsync" => Search(),
                "Dispose" => null,
                _ => throw new InvalidOperationException("Unexpected LDAP method in stage test: " + method.Name)
            });
            ((ScriptedLdapProxy)(object)connection).ObserveMethod = (method, args) =>
            {
                if (method.Name == "SearchAsync") SearchConstraints.Add(Assert.IsType<LdapSearchConstraints>(args![5]));
            };
            return connection;
        }

        private Task ConnectionStep(string stage) => stage == failStage ? Task.FromException(Failure()) : Task.CompletedTask;
        private Exception Failure() => new LdapException("private-message", CredentialRejection ? 49 : 10, "ldap://private-referral/DC=private-domain");

        private Task<ILdapSearchResults> Search()
        {
            var (stage, group, entry) = searches.Dequeue();
            var fail = stage == failStage && group == failGroup;
            if (fail && !failOnNext) return Task.FromException<ILdapSearchResults>(Failure());
            var consumed = false;
            return Task.FromResult(Proxy<ILdapSearchResults>(method =>
            {
                if (method.Name == "HasMoreAsync") return Task.FromResult(!consumed && (entry is not null || fail));
                if (method.Name == "NextAsync")
                {
                    consumed = true;
                    return fail ? Task.FromException<LdapEntry>(Failure()) : Task.FromResult(entry!);
                }
                throw new InvalidOperationException("Unexpected search-result method in stage test.");
            }));
        }
    }

    private static T Proxy<T>(Func<MethodInfo, object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, ScriptedLdapProxy>();
        ((ScriptedLdapProxy)(object)proxy).InvokeMethod = invoke;
        return proxy;
    }

    public class ScriptedLdapProxy : DispatchProxy
    {
        public Func<MethodInfo, object?> InvokeMethod { get; set; } = null!;
        public Action<MethodInfo, object?[]?>? ObserveMethod { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ObserveMethod?.Invoke(targetMethod!, args);
            return InvokeMethod(targetMethod!);
        }
    }
}
