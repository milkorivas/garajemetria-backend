using GarajeMetria.Application.Authentication;
using GarajeMetria.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Authentication;

internal sealed class SharedPolicyStartup(IServiceScopeFactory scopes, ITokenService tokens,
    AuthenticationAvailability availability, ILogger<SharedPolicyStartup> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = tokens; // Resolve and validate signing material before enabling the host.
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<RateLimitStore>().VerifyConfigurationAsync(timeout.Token);
                availability.SetAvailable(true);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                availability.SetAvailable(false);
                break;
            }
            catch (Exception)
            {
                availability.SetAvailable(false);
                logger.LogWarning("Authentication SQL policy verification unavailable; SQL-dependent requests remain closed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
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
