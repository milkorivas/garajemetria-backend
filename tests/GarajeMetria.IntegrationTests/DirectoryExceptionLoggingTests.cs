using System.Security.Authentication;
using GarajeMetria.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;
using Novell.Directory.Ldap;

namespace GarajeMetria.IntegrationTests;

public sealed class DirectoryExceptionLoggingTests
{
    [Theory]
    [InlineData(null, "invalid_credentials", null)]
    [InlineData("AcceptSecurityContext error, data 52E, v1234", "invalid_credentials", "52e")]
    [InlineData("AcceptSecurityContext error, data 525, v1234", "account_not_found", "525")]
    [InlineData("AcceptSecurityContext error, data 52f, v1234", "account_restricted", "52f")]
    [InlineData("AcceptSecurityContext error, data 530, v1234", "logon_hours_restricted", "530")]
    [InlineData("AcceptSecurityContext error, data 531, v1234", "workstation_restricted", "531")]
    [InlineData("AcceptSecurityContext error, data 532, v1234", "password_expired", "532")]
    [InlineData("AcceptSecurityContext error, data 533, v1234", "account_disabled", "533")]
    [InlineData("AcceptSecurityContext error, data 701, v1234", "account_expired", "701")]
    [InlineData("AcceptSecurityContext error, data 773, v1234", "password_change_required", "773")]
    [InlineData("AcceptSecurityContext error, data 775, v1234", "account_locked", "775")]
    [InlineData("AcceptSecurityContext error, data fff, v1234", "invalid_credentials", null)]
    [InlineData("AcceptSecurityContext error, data 775abc, v1234", "invalid_credentials", null)]
    [InlineData("AcceptSecurityContext error, data 775, data 532, v1234", "invalid_credentials", null)]
    [InlineData("data 775, v1234", "invalid_credentials", null)]
    public void CredentialFailuresHaveReadableReasonsAndOnlyAllowlistedSubCodes(string? diagnostic, string reason, string? subCode)
    {
        var logs = new RecordingLogProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var exception = new LdapException("private-exception", 49, diagnostic is null ? null! : diagnostic + " private-account private-password");
        factory.CreateLogger("DirectoryTest").LogDirectoryException(LogLevel.Information, "Health.ActiveDirectory.Bind", exception);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(reason, entry.Properties["Reason"]);
        Assert.Equal(subCode, entry.Properties["AdSubCode"]);
        Assert.Equal(49, entry.Properties["ErrorCode"]);
        Assert.Contains("cuenta de servicio", entry.Properties["Description"]!.ToString());
        Assert.DoesNotContain("private-", entry.Message);
        Assert.Null(entry.Exception);
        if (reason == "invalid_credentials")
            Assert.Equal("Active Directory rechazó las credenciales de la cuenta de servicio.", entry.Properties["Description"]);
    }

    [Fact]
    public void UserBindDescriptionDoesNotIdentifyItAsTheServiceAccount()
    {
        var logs = new RecordingLogProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        factory.CreateLogger("DirectoryTest").LogDirectoryException(LogLevel.Information, "ActiveDirectory.UserBindRejected",
            new LdapException("private-user", 49, "private-diagnostic"), serviceAccount: false);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal("Active Directory rechazó las credenciales de la cuenta de usuario.", entry.Properties["Description"]);
    }

    [Theory]
    [InlineData("Health.ActiveDirectory.Connect", "connection_failed")]
    [InlineData("Health.ActiveDirectory.Bind", "directory_error")]
    public void UnknownFailuresDoNotInventACredentialProblem(string operation, string reason)
    {
        var logs = new RecordingLogProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        factory.CreateLogger("DirectoryTest").LogDirectoryException(LogLevel.Warning, operation,
            new LdapException("private-message", 80, "AcceptSecurityContext error, data 775, private-password"));
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(reason, entry.Properties["Reason"]);
        Assert.Null(entry.Properties["AdSubCode"]);
        Assert.DoesNotContain("private-", entry.Message);
    }

    [Fact]
    public void OversizedDiagnosticsAreIgnoredAndTlsFailuresRemainDistinct()
    {
        var logs = new RecordingLogProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var logger = factory.CreateLogger("DirectoryTest");
        logger.LogDirectoryException(LogLevel.Warning, "Health.ActiveDirectory.Bind",
            new LdapException("private-message", 49, "AcceptSecurityContext error, data 775, " + new string('x', 4096)));
        Assert.Equal("invalid_credentials", logs.Entries.Single().Properties["Reason"]);
        logger.LogDirectoryException(LogLevel.Warning, "Health.ActiveDirectory.Connect",
            new LdapException("private-message", 91, "private-diagnostic", new AuthenticationException("private-certificate")));
        Assert.Equal("tls_validation_failed", logs.Entries.Last().Properties["Reason"]);
        Assert.All(logs.Entries, e => Assert.DoesNotContain("private-", e.Message));
    }
}
