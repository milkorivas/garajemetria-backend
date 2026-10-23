using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Diagnostics;
using GarajeMetria.Infrastructure.Authentication;
using GarajeMetria.Infrastructure.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GarajeMetria.IntegrationTests;

public sealed class LdapsFactAttribute : FactAttribute
{
    public LdapsFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GARAJEMETRIA_AD_TEST_CONFIG")))
            Skip = "Requires an isolated AD domain and external GARAJEMETRIA_AD_TEST_CONFIG; unit doubles do not verify LDAPS.";
    }
}

public sealed class DirectoryIntegrationTests
{
    [Fact]
    public void LdapFilterEscapesEveryUtf8ByteIncludingMetacharacters()
    {
        Assert.Equal("\\2A\\29\\28\\5C\\00", LdapCorporateDirectory.EscapeFilter("*)(\\\0"));
        Assert.Equal("\\C3\\BC", LdapCorporateDirectory.EscapeFilter("ü"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LdapsRejectsAnUntrustedServerCertificateBeforeSendingTheServicePasswordAndLogsTheFailure(bool healthProbe)
    {
        var logs = new RecordingLogProvider();
        using var logFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=untrusted-synthetic-server", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ObserveAsync();
        var options = new DirectoryOptions
        {
            Servers = ["127.0.0.1"],
            Port = port,
            TimeoutSeconds = 5,
            ServiceBindDn = "CN=synthetic",
            ServicePassword = "synthetic-only"
        };
        if (healthProbe)
        {
            var diagnostics = new HealthDiagnostics(new ConfigurationBuilder().Build(), options, logFactory.CreateLogger<HealthDiagnostics>());
            Assert.Equal(ModuleStatus.Error, await diagnostics.CheckActiveDirectoryAsync(timeout.Token));
        }
        else
        {
            var directory = new LdapCorporateDirectory(options, logFactory.CreateLogger<LdapCorporateDirectory>());
            var result = await directory.ReadAsync(Guid.NewGuid(), timeout.Token);
            Assert.Equal(DirectoryStatus.Unavailable, result.Status);
        }
        Assert.Equal(0, await server);
        var entry = Assert.Single(logs.Entries);
        Assert.True(entry.Properties.ContainsKey("ExceptionType"));
        Assert.True(entry.Properties.ContainsKey("Reason"));
        Assert.True(entry.Properties.ContainsKey("Description"));
        Assert.Contains("Connect", entry.Properties["Operation"]!.ToString());
        Assert.DoesNotContain(options.ServicePassword, entry.Message);
        Assert.DoesNotContain(options.ServiceBindDn, entry.Message);
        Assert.Null(entry.Exception);
        async Task<int> ObserveAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var tls = new SslStream(client.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
                return await tls.ReadAsync(new byte[128], timeout.Token);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException) { return 0; }
        }
    }

    [LdapsFact]
    public async Task RealLdapsAuthenticatesTheSameStableObjectAndReadsNestedMembershipWithoutPassword()
    {
        var path = Environment.GetEnvironmentVariable("GARAJEMETRIA_AD_TEST_CONFIG")!;
        var configuration = JsonSerializer.Deserialize<DirectoryTestConfiguration>(await File.ReadAllTextAsync(path))!;
        Assert.True(configuration.ConfirmedIsolatedTestDirectory);
        var adapter = new LdapCorporateDirectory(configuration.Directory, NullLogger<LdapCorporateDirectory>.Instance);
        foreach (var account in configuration.Accounts)
        {
            var authenticated = await adapter.AuthenticateAsync(account.Username, account.Password, default);
            Assert.Equal(DirectoryStatus.Valid, authenticated.Status);
            Assert.Equal(account.ObjectId, authenticated.Identity?.ObjectId);
            Assert.Equal(account.ExpectedRole, authenticated.Identity?.Role);
            var refreshed = await adapter.ReadAsync(account.ObjectId, default);
            Assert.Equal(DirectoryStatus.Valid, refreshed.Status);
            Assert.Equal(account.ExpectedRole, refreshed.Identity?.Role);
        }
        Assert.NotEmpty(configuration.Accounts);
        foreach (var objectId in configuration.RejectedObjectIds)
            Assert.Equal(DirectoryStatus.Rejected, (await adapter.ReadAsync(objectId, default)).Status);
        Assert.Equal(DirectoryStatus.NotFound, (await adapter.ReadAsync(configuration.AbsentObjectId, default)).Status);
    }

    public sealed class DirectoryTestConfiguration
    {
        public bool ConfirmedIsolatedTestDirectory { get; set; }
        public DirectoryOptions Directory { get; set; } = new();
        public TestAccount[] Accounts { get; set; } = [];
        public Guid[] RejectedObjectIds { get; set; } = [];
        public Guid AbsentObjectId { get; set; }
    }
    public sealed class TestAccount
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public Guid ObjectId { get; set; }
        public string? ExpectedRole { get; set; }
    }
}
