using GarajeMetria.Application.Diagnostics;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Stays;
using GarajeMetria.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Stays;

internal sealed class StayDailyClosureWorker(IServiceScopeFactory scopes, StayOptions options,
    LprPersistenceOptions persistence, ILogger<StayDailyClosureWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StayClosureCursor? after = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                StayClosurePage page;
                DateTimeOffset now;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(persistence.RequestTimeoutSeconds));
                    using var scope = scopes.CreateScope();
                    now = await scope.ServiceProvider.GetRequiredService<ILprClock>().GetUtcNowAsync(timeout.Token);
                    page = await scope.ServiceProvider.GetRequiredService<IStayClosureRepository>().ReadDueAsync(now, options.ClosureBatchSize, after, timeout.Token);
                    after = page.NextCursor;
                }
                foreach (var work in page.Items)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var scope = scopes.CreateScope();
                        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CloseStayDayCommand(work.PartitionId, work.LocalDate), stoppingToken);
                        StayTelemetry.Closure(result.Succeeded, now - work.DueAt);
                        if (!result.Succeeded) logger.LogWarning("Stay closure did not complete: code={Code}", result.Error!.Code);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        StayTelemetry.Closure(false, now - work.DueAt);
                        logger.LogSafeException(LogLevel.Error, "StayClosure.Partition", ex);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogSafeException(LogLevel.Warning, "StayClosure.Discovery", ex); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.ClosurePollSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
