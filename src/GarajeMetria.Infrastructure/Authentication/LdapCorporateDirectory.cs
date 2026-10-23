using System.Globalization;
using System.Net.Security;
using System.Text;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;
using Novell.Directory.Ldap;

namespace GarajeMetria.Infrastructure.Authentication;

public sealed class LdapCorporateDirectory(DirectoryOptions options, ILogger<LdapCorporateDirectory> logger) : ICorporateDirectory
{
    private static readonly string[] Attributes = ["objectGUID", "sAMAccountName", "displayName",
        "userAccountControl", "msDS-User-Account-Control-Computed", "pwdLastSet"];

    public Task<DirectoryResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(password) || password.Contains('\0') ? Task.FromResult(new DirectoryResult(DirectoryStatus.Rejected)) :
        RunAsync(async (service, server, ct) =>
        {
            var entry = await FindOneAsync(service, options.SearchBase,
                "(&(objectClass=user)(sAMAccountName=" + EscapeFilter(username) + "))", Attributes, ct);
            if (entry is null) return new(DirectoryStatus.Rejected);
            // Separate, non-anonymous user bind against the exact object resolved by the service.
            using var user = NewConnection();
            await user.ConnectAsync(server, options.Port, ct);
            try { await user.BindAsync(entry.Dn, password, ct); }
            catch (LdapException ex) when (ex.ResultCode == 49)
            {
                logger.LogDirectoryException(LogLevel.Information, "ActiveDirectory.UserBindRejected", ex, serviceAccount: false);
                return new(DirectoryStatus.Rejected);
            }
            var reread = await FindOneAsync(service, entry.Dn, "(objectClass=user)", Attributes, ct, LdapConnection.ScopeBase);
            if (reread is null || !ReadGuid(entry).Equals(ReadGuid(reread))) return new(DirectoryStatus.Rejected);
            return await DescribeAsync(service, reread, ct);
        }, cancellationToken);

    public Task<DirectoryResult> ReadAsync(Guid objectId, CancellationToken cancellationToken) =>
        RunAsync(async (service, _, ct) =>
        {
            var entry = await FindOneAsync(service, options.SearchBase,
                "(&(objectClass=user)(objectGUID=" + EscapeBytes(objectId.ToByteArray()) + "))", Attributes, ct);
            return entry is null ? new(DirectoryStatus.NotFound) : await DescribeAsync(service, entry, ct);
        }, cancellationToken);

    private async Task<DirectoryResult> RunAsync(Func<LdapConnection, string, CancellationToken, Task<DirectoryResult>> action,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var stage = "Connect";
        try
        {
            // Failover only while obtaining the service connection, before user credential validation.
            foreach (var server in options.Servers)
            {
                using var service = NewConnection();
                try
                {
                    stage = "Connect";
                    await service.ConnectAsync(server, options.Port, timeout.Token);
                    stage = "ServiceBind";
                    await service.BindAsync(options.ServiceBindDn, options.ServicePassword, timeout.Token);
                }
                catch (LdapException ex)
                {
                    logger.LogDirectoryException(LogLevel.Warning, "ActiveDirectory." + stage, ex);
                    continue;
                }
                stage = "ReadOrAuthenticate";
                return await action(service, server, timeout.Token);
            }
            return new(DirectoryStatus.Unavailable);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogDirectoryException(cancellationToken.IsCancellationRequested ? LogLevel.Information : LogLevel.Warning,
                cancellationToken.IsCancellationRequested ? "ActiveDirectory.Cancelled" : "ActiveDirectory.Timeout", ex);
            if (cancellationToken.IsCancellationRequested) throw;
            return new(DirectoryStatus.Unavailable);
        }
        catch (Exception ex) when (ex is LdapException or System.Security.Authentication.AuthenticationException or System.IO.IOException
            or FormatException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            logger.LogDirectoryException(LogLevel.Warning, "ActiveDirectory." + stage, ex);
            return new(DirectoryStatus.Unavailable);
        }
    }

    internal static LdapConnection NewConnection()
    {
        // Preserve chain, host and revocation validation using the library's async TLS support.
        return new LdapConnection(new LdapConnectionOptions().UseSsl().CheckCertificateRevocation()
            .ConfigureRemoteCertificateValidationCallback((_, _, _, errors) => errors == SslPolicyErrors.None));
    }

    private async Task<DirectoryResult> DescribeAsync(LdapConnection service, LdapEntry entry, CancellationToken ct)
    {
        var attributes = entry.GetAttributeSet();
        var uac = long.Parse(Required(attributes, "userAccountControl"), CultureInfo.InvariantCulture);
        var computed = long.Parse(Required(attributes, "msDS-User-Account-Control-Computed"), CultureInfo.InvariantCulture);
        var passwordLastSet = long.Parse(Required(attributes, "pwdLastSet"), CultureInfo.InvariantCulture);
        if ((uac & 0x2) != 0 || (computed & (0x10 | 0x800000)) != 0 || passwordLastSet == 0)
            return new(DirectoryStatus.Rejected);
        var guid = ReadGuid(entry);
        var admin = await IsMemberAsync(service, entry.Dn, options.AdministratorGroupDn, "web_GarajeMetria_Admin", ct);
        var advisor = await IsMemberAsync(service, entry.Dn, options.AdvisorGroupDn, "web_GarajeMetria_Advisor", ct);
        return new(DirectoryStatus.Valid, new(guid, Required(attributes, "sAMAccountName"),
            attributes.GetAttribute("displayName")?.StringValue ?? Required(attributes, "sAMAccountName"), admin, advisor));
    }

    private async Task<bool> IsMemberAsync(LdapConnection service, string userDn, string groupDn, string groupName, CancellationToken ct)
    {
        // Confirm the configured group can actually be read before treating absence as non-membership.
        var group = await FindOneAsync(service, groupDn, "(&(objectClass=group)(cn=" + EscapeFilter(groupName) + "))", ["cn"], ct, LdapConnection.ScopeBase);
        if (group is null) throw new InvalidOperationException("Authorizing group is unavailable.");
        var member = await FindOneAsync(service, userDn,
            "(memberOf:1.2.840.113556.1.4.1941:=" + EscapeFilter(groupDn) + ")", ["objectGUID"], ct, LdapConnection.ScopeBase);
        return member is not null;
    }

    private static async Task<LdapEntry?> FindOneAsync(LdapConnection connection, string searchBase, string filter,
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
}
