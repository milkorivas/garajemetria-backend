using System.Security.Authentication;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Novell.Directory.Ldap;

namespace GarajeMetria.Infrastructure.Diagnostics;

internal static class DirectoryExceptionLogging
{
    public static void LogDirectoryException(this ILogger logger, LogLevel level, string operation, Exception exception,
        bool serviceAccount = true)
    {
        var (reason, description, subCode) = Describe(operation, exception, serviceAccount);
        logger.Log(level,
            "Active Directory failure: {Description}; operation={Operation}, reason={Reason}, exceptionType={ExceptionType}, causeType={CauseType}, errorCode={ErrorCode}, adSubCode={AdSubCode}",
            description, operation, reason, exception.GetType().Name, exception.GetBaseException().GetType().Name,
            exception is LdapException ldap ? (int?)ldap.ResultCode : null, subCode);
    }

    private static (string Reason, string Description, string? SubCode) Describe(string operation, Exception exception, bool serviceAccount)
    {
        var account = serviceAccount ? "la cuenta de servicio" : "la cuenta de usuario";
        if (exception is LdapException { ResultCode: 49 } credentials)
        {
            var subCode = ReadSubCode(credentials.LdapErrorMessage);
            var (reason, description) = subCode switch
            {
                "525" => ("account_not_found", $"Active Directory indica que {account} no existe."),
                "52f" => ("account_restricted", $"Las restricciones de {account} impiden la autenticación en Active Directory."),
                "530" => ("logon_hours_restricted", $"El horario de acceso de {account} impide la autenticación en Active Directory."),
                "531" => ("workstation_restricted", $"Las restricciones de equipo de {account} impiden la autenticación en Active Directory."),
                "532" => ("password_expired", $"La contraseña de {account} ha caducado según Active Directory."),
                "533" => ("account_disabled", $"Active Directory indica que {account} está deshabilitada."),
                "701" => ("account_expired", $"Active Directory indica que {account} ha caducado."),
                "773" => ("password_change_required", $"Active Directory exige cambiar la contraseña de {account}."),
                "775" => ("account_locked", $"Active Directory indica que {account} está bloqueada."),
                _ => ("invalid_credentials", $"Active Directory rechazó las credenciales de {account}.")
            };
            return (reason, description, subCode);
        }
        if (exception is OperationCanceledException)
            return operation.EndsWith(".Cancelled", StringComparison.Ordinal)
                ? ("cancelled", "Se canceló la operación con Active Directory.", null)
                : ("timeout", "Se agotó el tiempo de espera de la operación con Active Directory.", null);
        if (exception.GetBaseException() is AuthenticationException)
            return ("tls_validation_failed", "No se pudo establecer una sesión TLS válida con Active Directory.", null);
        return exception switch
        {
            LdapException { ResultCode: 50 } => ("insufficient_permissions", "Active Directory denegó la operación por permisos insuficientes.", null),
            LdapException { ResultCode: 51 } => ("server_busy", "Active Directory está ocupado y no pudo completar la operación.", null),
            LdapException { ResultCode: 52 } => ("server_unavailable", "Active Directory indicó que el servicio no está disponible.", null),
            _ when operation.EndsWith(".Connect", StringComparison.Ordinal) || exception.GetBaseException() is IOException or System.Net.Sockets.SocketException =>
                ("connection_failed", "No se pudo establecer o mantener la conexión LDAPS con Active Directory.", null),
            _ => ("directory_error", "No se pudo completar la operación con Active Directory.", null)
        };
    }

    private static string? ReadSubCode(string? diagnostic)
    {
        // Parse only a bounded AD diagnostic and emit only an allowlisted code, never its text.
        if (diagnostic is null || diagnostic.Length > 4096 ||
            !diagnostic.Contains("AcceptSecurityContext", StringComparison.OrdinalIgnoreCase)) return null;
        var matches = Regex.Matches(diagnostic, @"\bdata[ \t]+([0-9a-f]{3})(?:[ \t]*,|[ \t]+|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        if (matches.Count != 1) return null;
        var code = matches[0].Groups[1].Value.ToLowerInvariant();
        return code is "525" or "52e" or "52f" or "530" or "531" or "532" or "533" or "701" or "773" or "775" ? code : null;
    }
}
