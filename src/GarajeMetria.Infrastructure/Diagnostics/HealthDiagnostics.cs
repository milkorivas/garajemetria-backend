using GarajeMetria.Application.Diagnostics;
using GarajeMetria.Infrastructure.Authentication;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Diagnostics;

internal sealed class HealthDiagnostics(IConfiguration configuration, DirectoryOptions directory, ILogger<HealthDiagnostics> logger) : IHealthDiagnostics
{
    public async Task<ModuleStatus> CheckSqlAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("Authentication");
        if (string.IsNullOrWhiteSpace(connectionString)) return ModuleStatus.NotConfigured;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var settings = new SqlConnectionStringBuilder(connectionString) { ConnectRetryCount = 0 };
            settings.ConnectTimeout = settings.ConnectTimeout == 0 ? 5 : Math.Min(5, settings.ConnectTimeout);
            await using var connection = new SqlConnection(settings.ConnectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 5;
            await command.ExecuteScalarAsync(timeout.Token);
            return ModuleStatus.Ok;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogSafeException(LogLevel.Information, "Health.Sql.Cancelled", ex);
            throw;
        }
        catch (Exception ex) when (ex is SqlException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogSafeException(LogLevel.Information, "Health.Sql.Probe", ex, ex is SqlException sql ? sql.Number : null);
            return ModuleStatus.Error;
        }
    }

    public async Task<ModuleStatus> CheckActiveDirectoryAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(5, directory.TimeoutSeconds)));
        var stage = "Connect";
        try
        {
            foreach (var server in directory.Servers)
            {
                using var connection = LdapCorporateDirectory.NewConnection();
                try
                {
                    stage = "Connect";
                    await connection.ConnectAsync(server, directory.Port, timeout.Token);
                    stage = "Bind";
                    await connection.BindAsync(directory.ServiceBindDn, directory.ServicePassword, timeout.Token);
                    return ModuleStatus.Ok;
                }
                catch (Novell.Directory.Ldap.LdapException ex)
                {
                    logger.LogDirectoryException(LogLevel.Information, "Health.ActiveDirectory." + stage, ex);
                }
            }
            return ModuleStatus.Error;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDirectoryException(LogLevel.Information, "Health.ActiveDirectory.Cancelled", ex);
            throw;
        }
        catch (Exception ex) when (ex is Novell.Directory.Ldap.LdapException or OperationCanceledException
            or System.Security.Authentication.AuthenticationException or IOException or InvalidOperationException
            or System.Net.Sockets.SocketException)
        {
            logger.LogDirectoryException(LogLevel.Information, "Health.ActiveDirectory." + stage, ex);
            return ModuleStatus.Error;
        }
    }
}
