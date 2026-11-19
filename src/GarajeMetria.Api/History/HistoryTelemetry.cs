using System.Diagnostics;
using System.Diagnostics.Metrics;
using GarajeMetria.Api.Authentication;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.History;

namespace GarajeMetria.Api.History;

public sealed class HistoryTelemetry : IDisposable
{
    public const string RowsItem = "GarajeMetria.History.Rows";
    public const string FailureItem = "GarajeMetria.History.Failure";
    private readonly Meter meter = new("GarajeMetria.History");
    private readonly Counter<long> requests;
    private readonly Histogram<double> duration;
    private readonly Counter<long> rows;
    private readonly UpDownCounter<long> activeExports;
    public HistoryTelemetry()
    {
        requests = meter.CreateCounter<long>("garajemetria.history.requests");
        duration = meter.CreateHistogram<double>("garajemetria.history.duration", "s");
        rows = meter.CreateCounter<long>("garajemetria.history.rows");
        activeExports = meter.CreateUpDownCounter<long>("garajemetria.history.active_export_requests");
    }
    public void ExportChanged(int delta) => activeExports.Add(delta);
    public void Record(string operation, int status, double seconds, long count, string outcome)
    {
        var tags = new TagList { { "operation", operation }, { "status_code", status }, { "outcome", outcome } };
        requests.Add(1, tags); duration.Record(seconds, tags);
        rows.Add(count, tags);
    }
    public void Dispose() => meter.Dispose();
}
public sealed class HistoryTelemetryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, HistoryTelemetry telemetry, HistoryOptions options, ILogger<HistoryTelemetryMiddleware> logger)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<HistoryMetadata>() is null) { await next(context); return; }
        context.Response.Headers.CacheControl = "no-store";
        var operation = context.GetEndpoint()!.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>()?.EndpointName ?? "History";
        var start = Stopwatch.GetTimestamp();
        var exporting = operation == "ExportHistory";
        var original = context.RequestAborted;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(original);
        budget.CancelAfter(TimeSpan.FromSeconds(exporting ? options.ExportTimeoutSeconds : options.QueryTimeoutSeconds));
        context.RequestAborted = budget.Token;
        if (exporting) telemetry.ExportChanged(1);
        var status = 500;
        try { await next(context); status = context.Response.StatusCode; }
        catch (OperationCanceledException) when (!original.IsCancellationRequested && budget.IsCancellationRequested && !context.Response.HasStarted)
        {
            status = 503;
            context.RequestAborted = original;
            await ApiProblems.FromFailure(new(FailureCode.ServiceUnavailable)).ExecuteAsync(context);
        }
        catch (PersistenceFailureException ex) when (!original.IsCancellationRequested && budget.IsCancellationRequested && !context.Response.HasStarted)
        {
            status = 503;
            context.RequestAborted = original;
            await ApiProblems.FromFailure(new(ex.Code == FailureCode.OperationOutcomeUnknown ? ex.Code : FailureCode.ServiceUnavailable)).ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { status = 499; throw; }
        finally
        {
            context.RequestAborted = original;
            var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
            if (exporting) telemetry.ExportChanged(-1);
            telemetry.Record(operation, status, elapsed, context.Items[HistoryTelemetry.RowsItem] is int count ? count : 0,
                context.Items[HistoryTelemetry.FailureItem] as string ?? (status < 400 ? "success" : "rejected"));
            logger.LogInformation("History request: operation={Operation}, status={Status}, elapsedSeconds={ElapsedSeconds}", operation, status, elapsed);
        }
    }
}
