using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace GarajeMetria.Api.Lpr;

public sealed class LprTelemetry : IDisposable
{
    public const string MeterName = "GarajeMetria.Lpr";
    private readonly Meter meter = new(MeterName);
    private readonly ILogger<LprTelemetry> logger;
    private readonly Counter<long> requests;
    private readonly Histogram<double> duration;
    internal const string ReceiptItem = "GarajeMetria.Lpr.Receipt";

    public LprTelemetry(ILogger<LprTelemetry> logger)
    {
        this.logger = logger;
        requests = meter.CreateCounter<long>("garajemetria.lpr.requests", "{request}");
        duration = meter.CreateHistogram<double>("garajemetria.lpr.request.duration", "s");
    }

    internal void Record(LprTraffic traffic, int status, string outcome, string plateStatus, double seconds)
    {
        var tags = new TagList { { "traffic", traffic.ToString().ToLowerInvariant() }, { "outcome", outcome },
            { "status_code", status }, { "plate_status", plateStatus } };
        // Instruments and all label values are fixed by the server, never by a payload or identity.
        requests.Add(1, tags);
        duration.Record(seconds, tags);
        logger.LogInformation("LPR request completed: traffic={Traffic}, outcome={Outcome}, status={StatusCode}, plateStatus={PlateStatus}, elapsed={ElapsedMs} ms",
            traffic, outcome, status, plateStatus, seconds * 1000);
    }
    public void Dispose() => meter.Dispose();
}

public sealed class LprTelemetryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, LprTelemetry telemetry)
    {
        LprTraffic? traffic = LprProblems.IsTokenRequest(context) ? LprTraffic.Token :
            context.Request.Path.Equals("/api/lpr/events", StringComparison.OrdinalIgnoreCase) ? LprTraffic.Event :
            context.Request.Path.StartsWithSegments("/api/lpr/devices", StringComparison.OrdinalIgnoreCase) ? LprTraffic.Administration : null;
        if (traffic is null) { await next(context); return; }
        var started = Stopwatch.GetTimestamp();
        var failure = false;
        var cancelled = false;
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { cancelled = true; throw; }
        catch { failure = true; throw; }
        finally
        {
            var status = cancelled ? 499 : failure ? 500 : context.Response.StatusCode;
            var receipt = context.Items[LprTelemetry.ReceiptItem] as LprEventResponse;
            var outcome = cancelled ? "cancelled" : failure ? "failed" : receipt is not null ? receipt.Duplicate ? "duplicate" : "accepted" : status switch
            {
                401 => "unauthorized",
                403 => "forbidden",
                409 => "conflict",
                422 => "outside_window",
                429 => "rate_limited",
                503 => "unavailable",
                >= 500 => "failed",
                >= 400 => "invalid",
                _ => "success"
            };
            telemetry.Record(traffic.Value, status, outcome, receipt?.PlateStatus is "valid" or "invalid" ? receipt.PlateStatus : "none",
                Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }
}
