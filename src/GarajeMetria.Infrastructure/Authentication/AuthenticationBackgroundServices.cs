using GarajeMetria.Application.Authentication;
using GarajeMetria.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Authentication;

internal sealed class SharedPolicyStartup(IServiceScopeFactory scopes, ITokenService tokens) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = tokens; // Resolve and validate signing material before enabling the host.
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RateLimitStore>().VerifyConfigurationAsync(cancellationToken);
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class AuthenticationCleanupService(IServiceScopeFactory scopes, ILogger<AuthenticationCleanupService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var owner = Guid.NewGuid().ToString("N");
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                CleanupResult result;
                do
                {
                    using var scope = scopes.CreateScope();
                    result = await scope.ServiceProvider.GetRequiredService<ISender>()
                        .Send(new CleanupAuthenticationCommand(owner), stoppingToken);
                    if (result.Acquired) logger.LogInformation("Authentication cleanup batch: refresh={Refresh}, sessions={Sessions}, audit={Audit}",
                        result.RefreshRecords, result.Sessions, result.AuditEntries);
                } while (result.More && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogError("Authentication cleanup could not complete its batch; no completion was recorded."); }
        }
    }
}
