using GarajeMetria.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Diagnostics;

// Shared across request scopes; observes results without running its own network probes.
internal sealed class HealthStatusTracker(ILogger<HealthStatusTracker> logger)
{
    private readonly object gate = new();
    private readonly Dictionary<string, ModuleStatus> statuses = [];

    public void Observe(string module, ModuleStatus status)
    {
        lock (gate)
        {
            var observed = statuses.TryGetValue(module, out var previous);
            statuses[module] = status;
            if (observed && previous == status)
            {
                if (status != ModuleStatus.Ok)
                    logger.LogDebug("Dependency remains unhealthy: module={Module}, status={Status}", module, status);
                return;
            }
            if (status != ModuleStatus.Ok)
                logger.LogWarning("Dependency unavailable: module={Module}, status={Status}", module, status);
            else if (observed)
                logger.LogInformation("Dependency recovered: module={Module}, status={Status}", module, status);
        }
    }
}

internal sealed class HealthDiagnosticsLoggingDecorator(IHealthDiagnostics inner, HealthStatusTracker tracker) : IHealthDiagnostics
{
    public async Task<ModuleStatus> CheckSqlAsync(CancellationToken cancellationToken)
    {
        var status = await inner.CheckSqlAsync(cancellationToken);
        tracker.Observe("sql", status);
        return status;
    }

    public async Task<ModuleStatus> CheckActiveDirectoryAsync(CancellationToken cancellationToken)
    {
        var status = await inner.CheckActiveDirectoryAsync(cancellationToken);
        tracker.Observe("activeDirectory", status);
        return status;
    }
}
