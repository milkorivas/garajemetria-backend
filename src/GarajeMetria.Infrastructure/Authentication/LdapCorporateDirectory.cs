using System.Globalization;
using System.Net.Security;
using System.Text;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;
using Novell.Directory.Ldap;

namespace GarajeMetria.Infrastructure.Authentication;

public sealed class LdapCorporateDirectory : ICorporateDirectory
{
    private readonly DirectoryOptions options;
    private readonly ILogger<LdapCorporateDirectory> logger;
    private readonly Func<ILdapConnection> createConnection;
    private static readonly string[] Attributes = ["objectGUID", "sAMAccountName", "displayName",
        "userAccountControl", "msDS-User-Account-Control-Computed", "pwdLastSet"];

    public LdapCorporateDirectory(DirectoryOptions options, ILogger<LdapCorporateDirectory> logger)
        : this(options, logger, NewConnection) { }

    internal LdapCorporateDirectory(DirectoryOptions options, ILogger<LdapCorporateDirectory> logger, Func<ILdapConnection> createConnection)
    {
        this.options = options;
        this.logger = logger;
        this.createConnection = createConnection;
    }

    public Task<DirectoryResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(password) || password.Contains('\0') ? Task.FromResult(new DirectoryResult(DirectoryStatus.Rejected)) :
        RunAsync(async (service, server, stage, ct) =>
        {
            stage.Start("UserSearch");
            var entry = await FindOneAsync(service, options.SearchBase,
                "(&(objectClass=user)(sAMAccountName=" + EscapeFilter(username) + "))", Attributes, ct);
            if (entry is null) return new(DirectoryStatus.Rejected);
            // Separate, non-anonymous user bind against the exact object resolved by the service.
            stage.Start("UserConnect");
            using var user = createConnection();
            await user.ConnectAsync(server, options.Port, ct);
            stage.Start("UserBind");
            try { await user.BindAsync(entry.Dn, password, ct); }
            catch (LdapException ex) when (ex.ResultCode == 49)
            {
                logger.LogDirectoryException(LogLevel.Information, stage.Operation, ex, serviceAccount: false);
                return new(DirectoryStatus.Rejected);
            }
            stage.Start("UserRead");
            var reread = await FindOneAsync(service, entry.Dn, "(objectClass=user)", Attributes, ct, LdapConnection.ScopeBase);
            if (reread is null || !ReadGuid(entry).Equals(ReadGuid(reread))) return new(DirectoryStatus.Rejected);
            return await DescribeAsync(service, reread, stage, ct);
        }, cancellationToken);

    public Task<DirectoryResult> ReadAsync(Guid objectId, CancellationToken cancellationToken) =>
        RunAsync(async (service, _, stage, ct) =>
        {
            stage.Start("UserSearch");
            var entry = await FindOneAsync(service, options.SearchBase,
                "(&(objectClass=user)(objectGUID=" + EscapeBytes(objectId.ToByteArray()) + "))", Attributes, ct);
            return entry is null ? new(DirectoryStatus.NotFound) : await DescribeAsync(service, entry, stage, ct);
        }, cancellationToken);

    private async Task<DirectoryResult> RunAsync(Func<ILdapConnection, string, OperationStage, CancellationToken, Task<DirectoryResult>> action,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        // This context belongs to one call: this adapter is registered as a singleton.
        var stage = new OperationStage(logger);
        try
        {
            // Failover only while obtaining the service connection, before user credential validation.
            foreach (var server in options.Servers)
            {
                stage.Start("Connect");
                using var service = createConnection();
                try
                {
                    await service.ConnectAsync(server, options.Port, timeout.Token);
                    stage.Start("ServiceBind");
                    await service.BindAsync(options.ServiceBindDn, options.ServicePassword, timeout.Token);
                }
                catch (LdapException ex)
                {
                    logger.LogDirectoryException(LogLevel.Warning, stage.Operation, ex);
                    continue;
                }
                return await action(service, server, stage, timeout.Token);
            }
            return new(DirectoryStatus.Unavailable);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogDirectoryException(cancellationToken.IsCancellationRequested ? LogLevel.Information : LogLevel.Warning,
                stage.Operation + (cancellationToken.IsCancellationRequested ? ".Cancelled" : ".Timeout"), ex,
                stage.ServiceAccount, stage.GroupRole);
            if (cancellationToken.IsCancellationRequested) throw;
            return new(DirectoryStatus.Unavailable);
        }
        catch (Exception ex) when (ex is LdapException or System.Security.Authentication.AuthenticationException or System.IO.IOException
            or FormatException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            logger.LogDirectoryException(LogLevel.Warning, stage.Operation, ex, stage.ServiceAccount, stage.GroupRole);
            return new(DirectoryStatus.Unavailable);
        }
    }

    internal static LdapConnection NewConnection()
    {
        // Preserve chain, host and revocation validation using the library's async TLS support.
        return new LdapConnection(new LdapConnectionOptions().UseSsl().CheckCertificateRevocation()
            .ConfigureRemoteCertificateValidationCallback((_, _, _, errors) => errors == SslPolicyErrors.None));
    }

    private async Task<DirectoryResult> DescribeAsync(ILdapConnection service, LdapEntry entry, OperationStage stage, CancellationToken ct)
    {
        stage.Start("UserAttributes");
        var attributes = entry.GetAttributeSet();
        var uac = long.Parse(Required(attributes, "userAccountControl"), CultureInfo.InvariantCulture);
        var computed = long.Parse(Required(attributes, "msDS-User-Account-Control-Computed"), CultureInfo.InvariantCulture);
        var passwordLastSet = long.Parse(Required(attributes, "pwdLastSet"), CultureInfo.InvariantCulture);
        if ((uac & 0x2) != 0 || (computed & (0x10 | 0x800000)) != 0 || passwordLastSet == 0)
            return new(DirectoryStatus.Rejected);
        var guid = ReadGuid(entry);
        var admin = await IsMemberAsync(service, entry.Dn, options.AdministratorGroupDn, "web_GarajeMetria_Admin", "Administrator", stage, ct);
        var advisor = await IsMemberAsync(service, entry.Dn, options.AdvisorGroupDn, "web_GarajeMetria_Advisor", "Advisor", stage, ct);
        stage.Start("UserProfile");
        return new(DirectoryStatus.Valid, new(guid, Required(attributes, "sAMAccountName"),
            attributes.GetAttribute("displayName")?.StringValue ?? Required(attributes, "sAMAccountName"), admin, advisor));
    }

    private async Task<bool> IsMemberAsync(ILdapConnection service, string userDn, string groupDn, string groupName,
        string groupRole, OperationStage stage, CancellationToken ct)
    {
        stage.Start("GroupLookup", groupRole);
        // Confirm the configured group can actually be read before treating absence as non-membership.
        var group = await FindOneAsync(service, groupDn, "(&(objectClass=group)(cn=" + EscapeFilter(groupName) + "))", ["cn"], ct, LdapConnection.ScopeBase);
        if (group is null) throw new InvalidOperationException("Authorizing group is unavailable.");
        stage.Start("GroupMembership", groupRole);
        var member = await FindOneAsync(service, userDn,
            "(memberOf:1.2.840.113556.1.4.1941:=" + EscapeFilter(groupDn) + ")", ["objectGUID"], ct, LdapConnection.ScopeBase);
        return member is not null;
    }

    private static async Task<LdapEntry?> FindOneAsync(ILdapConnection connection, string searchBase, string filter,
        string[] attributes, CancellationToken ct, int scope = LdapConnection.ScopeSub)
    {
        var results = await connection.SearchAsync(searchBase, scope, filter, attributes, false, ct);
        LdapEntry? result = null;
        while (await results.HasMoreAsync(ct))
        {
            var entry = await results.NextAsync(ct);
            if (result is not null) throw new InvalidOperationException("Directory search is ambiguous.");
            result = entry;
        }
        return result;
    }

    private static string Required(LdapAttributeSet attributes, string name) => attributes.GetAttribute(name)?.StringValue
        ?? throw new InvalidOperationException("A required directory attribute is unavailable.");
    private static Guid ReadGuid(LdapEntry entry) => new(entry.GetAttributeSet().GetAttribute("objectGUID")?.ByteValue
        ?? throw new InvalidOperationException("Directory identity is unavailable."));
    public static string EscapeFilter(string value) => EscapeBytes(Encoding.UTF8.GetBytes(value));
    private static string EscapeBytes(byte[] value) => string.Concat(value.Select(b => "\\" + b.ToString("X2", CultureInfo.InvariantCulture)));

    private sealed class OperationStage(ILogger logger)
    {
        private string name = "Connect";
        public string Operation => "ActiveDirectory." + name;
        public bool ServiceAccount => name is not ("UserConnect" or "UserBind");
        public string? GroupRole { get; private set; }

        public void Start(string stage, string? groupRole = null)
        {
            name = stage;
            GroupRole = groupRole;
            logger.LogDebug("Active Directory stage started: operation={Operation}, groupRole={GroupRole}", Operation, GroupRole);
        }
    }
}
