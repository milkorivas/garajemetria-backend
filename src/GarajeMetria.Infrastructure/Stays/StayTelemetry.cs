using System.Diagnostics;
using System.Diagnostics.Metrics;
using GarajeMetria.Application.Stays;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Stays;

internal sealed class StayTelemetry(ILogger<StayTelemetry> logger) : IStayProcessingObserver
{
    internal static readonly Meter Meter = new("GarajeMetria.Stays", "1.0.0");
    private static readonly Histogram<double> CalculationDuration = Meter.CreateHistogram<double>("garajemetria.stays.calculation.duration", "s");
    private static readonly Histogram<int> ContextReadings = Meter.CreateHistogram<int>("garajemetria.stays.context.readings", "{reading}");
    private static readonly Counter<long> ProjectionAttempts = Meter.CreateCounter<long>("garajemetria.stays.projection.attempts", "{projection}");
    private static readonly Histogram<double> CatalogHold = Meter.CreateHistogram<double>("garajemetria.persistence.catalog.hold.duration", "s");
    private static readonly Histogram<double> CatalogWait = Meter.CreateHistogram<double>("garajemetria.persistence.catalog.wait.duration", "s");
    private static readonly Counter<long> Rollbacks = Meter.CreateCounter<long>("garajemetria.persistence.transaction.rollbacks", "{rollback}");
    private static readonly Counter<long> Estimated = Meter.CreateCounter<long>("garajemetria.stays.estimated.boundary.attempts", "{boundary}");
    private static readonly Counter<long> Closures = Meter.CreateCounter<long>("garajemetria.stays.closure.attempts", "{closure}");
    private static readonly Histogram<double> BacklogAge = Meter.CreateHistogram<double>("garajemetria.stays.closure.pending.age", "s");

    public void Recalculated(StayProjectionCause cause, int readings, int created, int updated, int absorbed, int estimated, TimeSpan elapsed)
    {
        var name = cause switch { StayProjectionCause.Ingestion => "ingestion", StayProjectionCause.DailyClosure => "daily_closure", _ => "initial_projection" };
        var tags = new TagList { { "cause", name } };
        CalculationDuration.Record(elapsed.TotalSeconds, tags); ContextReadings.Record(readings, tags);
        Estimated.Add(estimated, tags);
        ProjectionAttempts.Add(created, new TagList { { "change", "created" } });
        ProjectionAttempts.Add(updated, new TagList { { "change", "updated" } });
        ProjectionAttempts.Add(absorbed, new TagList { { "change", "absorbed" } });
        logger.LogInformation("Stay recalculation staged: cause={Cause}, readings={Readings}, created={Created}, updated={Updated}, absorbed={Absorbed}, estimated={Estimated}, elapsedMs={ElapsedMs}",
            name, readings, created, updated, absorbed, estimated, elapsed.TotalMilliseconds);
    }

    internal static void CatalogReleased(TimeSpan elapsed, bool failed) =>
        CatalogHold.Record(elapsed.TotalSeconds, new TagList { { "outcome", failed ? "failed" : "completed" } });
    internal static void CatalogWaited(TimeSpan elapsed) => CatalogWait.Record(elapsed.TotalSeconds);
    internal static void RolledBack() => Rollbacks.Add(1);
    internal static void Closure(bool success, TimeSpan age)
    {
        Closures.Add(1, new TagList { { "outcome", success ? "completed" : "failed" } });
        BacklogAge.Record(Math.Max(0, age.TotalSeconds));
    }
}
