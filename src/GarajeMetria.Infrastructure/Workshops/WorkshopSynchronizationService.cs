using GarajeMetria.Application.Diagnostics;
using GarajeMetria.Application.Workshops;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Workshops;

internal sealed class WorkshopSynchronizationService(IServiceScopeFactory scopes, WorkshopCatalogOptions options,
    ILogger<WorkshopSynchronizationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopes.CreateScope();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try
            {
                var result = await scope.ServiceProvider.GetRequiredService<ISender>()
                    .Send(new SynchronizeWorkshopsCommand(), timeout.Token);
                logger.LogInformation("Workshop synchronization: published={Published}, lastPublishedAt={LastPublishedAt}, added={Added}, changed={Changed}, deactivated={Deactivated}, reactivated={Reactivated}",
                    result.Published, result.LastPublishedAt, result.Added, result.Changed, result.Deactivated, result.Reactivated);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogSafeException(LogLevel.Warning, "Workshops.Synchronization", ex);
                // Use a fresh scope/token: the failed operation may have an unusable context.
                try
                {
                    using var statusScope = scopes.CreateScope();
                    using var statusTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    statusTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var last = await statusScope.ServiceProvider.GetRequiredService<IWorkshopCatalogStore>().LastPublishedAsync(statusTimeout.Token);
                    logger.LogWarning("Workshop catalog retained: lastPublishedAt={LastPublishedAt}, ageSeconds={AgeSeconds}",
                        last, last is null ? null : (double?)(DateTimeOffset.UtcNow - last.Value).TotalSeconds);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception statusError) { logger.LogSafeException(LogLevel.Warning, "Workshops.Status", statusError); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
