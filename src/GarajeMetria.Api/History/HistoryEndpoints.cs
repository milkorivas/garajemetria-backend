using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GarajeMetria.Api.Authentication;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.History;
using GarajeMetria.Domain.Authentication;
using MediatR;

namespace GarajeMetria.Api.History;

public static class HistoryEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    private static readonly HashSet<string> Arrays = ["workshopIds", "deviceIds", "locationIds"];
    private static readonly HashSet<string> Numbers = ["pageNumber", "pageSize", "confidenceMin", "confidenceMax"];
    private static readonly HashSet<string> Fields = typeof(HistoryFilter).GetProperties()
        .Select(x => JsonNamingPolicy.CamelCase.ConvertName(x.Name)).ToHashSet(StringComparer.Ordinal);

    public static void MapHistoryEndpoints(this WebApplication app)
    {
        Map(app, "/api/lpr/events", HistoryKind.Events, "SearchLprEvents");
        Map(app, "/api/lpr/events/{eventId:guid}", HistoryKind.Event, "GetLprEvent");
        Map(app, "/api/stays", HistoryKind.Stays, "SearchStays");
        Map(app, "/api/stays/{stayId:guid}", HistoryKind.Stay, "GetStay");
        Map(app, "/api/stays/{stayId:guid}/observations", HistoryKind.Observations, "ListStayObservations");
        Map(app, "/api/vehicles/present", HistoryKind.Present, "ListPresentVehicles");
        Map(app, "/api/vehicles/timeline", HistoryKind.Timeline, "GetVehicleTimeline");
        Map(app, "/api/history/devices", HistoryKind.Devices, "ListHistoryDevices");
        Map(app, "/api/history/locations", HistoryKind.Locations, "ListHistoryLocations");
        Describe(app.MapPost("/api/history/exports", ExportAsync), "ExportHistory")
            .Accepts<ExportBody>("application/json").Produces(200, contentType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
            .ProducesProblem(413).ProducesProblem(415).ProducesProblem(422);
    }
    private static void Map(WebApplication app, string route, HistoryKind kind, string name) =>
        Describe(app.MapGet(route, (HttpContext context, ISender sender) => ReadAsync(context, sender, kind)), name)
            .Produces(200, typeof(HistoryPageResponse), "application/json");
    private static RouteHandlerBuilder Describe(RouteHandlerBuilder route, string name) => route
        .RequireAuthorization("HistoryReader").WithName(name).WithTags("Vehicle History").WithMetadata(new HistoryMetadata())
        .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
        .ProducesProblem(429).ProducesProblem(500).ProducesProblem(503);

    private static async Task<IResult> ReadAsync(HttpContext context, ISender sender, HistoryKind kind)
    {
        context.Response.Headers.CacheControl = "no-store";
        HistoryFilter filter;
        try
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in context.Request.Query)
            {
                if (!Fields.Contains(key) || !Arrays.Contains(key) && value.Count != 1) return Invalid();
                if (Arrays.Contains(key)) values[key] = value.ToArray();
                else if (Numbers.Contains(key))
                {
                    if (!decimal.TryParse(value[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) return Invalid();
                    values[key] = number;
                }
                else
                {
                    if (key is "from" or "to" && !HasOffset(value[0])) return Invalid();
                    values[key] = value[0];
                }
            }
            filter = JsonSerializer.Deserialize<HistoryFilter>(JsonSerializer.Serialize(values), Json)!;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException) { return Invalid(); }
        var id = context.Request.RouteValues.TryGetValue("eventId", out var eventId) ? eventId :
            context.Request.RouteValues.TryGetValue("stayId", out var stayId) ? stayId : null;
        HistoryRequest request = kind switch
        {
            HistoryKind.Event => new GetLprEventQuery(Guid.Parse((string)id!), filter),
            HistoryKind.Stay => new GetStayQuery(Guid.Parse((string)id!), filter),
            HistoryKind.Observations => new ListStayObservationsQuery(Guid.Parse((string)id!), filter),
            HistoryKind.Devices => new ListHistoryDevicesQuery(filter),
            HistoryKind.Locations => new ListHistoryLocationsQuery(filter),
            _ => HistoryRequests.Create(kind, filter)
        };
        var result = await sender.Send(request, context.RequestAborted);
        if (!result.Succeeded) return Failure(context, result.Error!);
        var page = result.Value!;
        context.Items[HistoryTelemetry.RowsItem] = page.Items.Count;
        if (kind == HistoryKind.Event) return Results.Json(Event(page.Items.Single().Event!) with { AsOfUtc = page.AsOfUtc, UpdatedAtUtc = page.UpdatedAtUtc });
        if (kind == HistoryKind.Stay) return Results.Json(new HistoryStayDetail(page.RequestedStayId!.Value,
            page.ResolvedStayId!.Value, page.WasReplaced, page.Items.Single().Stay!,
            $"/api/stays/{page.ResolvedStayId:D}/observations", page.AsOfUtc, page.UpdatedAtUtc));
        return Results.Json(new HistoryPageResponse(page.Items.Select(x => Item(x, kind)).ToArray(), page.PageNumber,
            page.PageSize, page.TotalCount, page.TotalPages, page.AsOfUtc, page.UpdatedAtUtc, page.TimeZone,
            page.Window?.Start, page.Window?.End, page.RequestedStayId, page.ResolvedStayId, page.WasReplaced));
    }
    private static HistoryEventResponse Event(HistoryEvent e) => new(e.EventId, e.PlateOriginal, e.PlateNormalized,
        e.PlateStatus, e.CapturedAtUtc, e.ReceivedAtUtc, e.Workshop, e.Device,
        new(e.Location.LocationId, e.Location.Description, e.Location.RecordedAt,
            JsonNamingPolicy.CamelCase.ConvertName(e.Location.Provenance.ToString())), e.Direction, e.Confidence, e.StayId);
    private static object Item(HistoryItem item, HistoryKind kind) => kind == HistoryKind.Timeline ?
        new { item.Kind, item.OccurredAtUtc, Event = item.Event is null ? null : Event(item.Event), item.Stay } :
        item.Event is not null ? Event(item.Event) : (object?)item.Stay ?? item.Selector!;

    public sealed record HistoryPageResponse(object[] Items, int PageNumber, int PageSize, long TotalCount, long TotalPages,
        DateTimeOffset AsOfUtc, DateTimeOffset UpdatedAtUtc, string TimeZone, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc,
        Guid? RequestedStayId, Guid? ResolvedStayId, bool WasReplaced);
    public sealed record HistoryListResponse<T>(T[] Items, int PageNumber, int PageSize, long TotalCount, long TotalPages,
        DateTimeOffset AsOfUtc, DateTimeOffset UpdatedAtUtc, string TimeZone, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc,
        Guid? RequestedStayId, Guid? ResolvedStayId, bool WasReplaced);
    public sealed record HistoryLocationResponse(Guid? LocationId, string? Description, DateTimeOffset? RecordedAt, string Provenance);
    public sealed record HistoryEventResponse(Guid EventId, string PlateOriginal, string? PlateNormalized, string PlateStatus,
        DateTimeOffset CapturedAtUtc, DateTimeOffset ReceivedAtUtc, HistoryLabel Workshop, HistoryLabel Device,
        HistoryLocationResponse Location, string? Direction, decimal? Confidence, Guid? StayId,
        DateTimeOffset? AsOfUtc = null, DateTimeOffset? UpdatedAtUtc = null);
    public sealed record HistoryTimelineItem(string Kind, DateTimeOffset OccurredAtUtc, HistoryEventResponse? Event, HistoryStay? Stay);
    public sealed record HistoryStayDetail(Guid RequestedStayId, Guid ResolvedStayId, bool WasReplaced, HistoryStay Stay,
        string Observations, DateTimeOffset AsOfUtc, DateTimeOffset UpdatedAtUtc);
    public sealed record ExportBody(string Kind, HistoryFilter Filters, string? Order);
    private static async Task<IResult> ExportAsync(HttpContext context, ISender sender)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Query.Count > 0) return Invalid();
        if (!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media) ||
            !media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            media.Charset.HasValue && !media.Charset.Value!.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Headers.ContentEncoding.Any(x => x != "identity"))
            return ApiProblems.Create(415, "unsupported_media_type", "Se requiere JSON UTF-8.");
        if (context.Request.ContentLength > 32768) return TooLarge();
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int count;
        while ((count = await context.Request.Body.ReadAsync(bytes, context.RequestAborted)) != 0)
        {
            if (buffer.Length + count > 32768) return TooLarge();
            await buffer.WriteAsync(bytes.AsMemory(0, count), context.RequestAborted);
        }
        ExportBody? body;
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (!UniqueFields(document.RootElement) || document.RootElement.ValueKind != JsonValueKind.Object) return Invalid();
            if (!document.RootElement.TryGetProperty("filters", out var filters) || filters.ValueKind != JsonValueKind.Object ||
                filters.TryGetProperty("pageNumber", out _) || filters.TryGetProperty("pageSize", out _) ||
                filters.TryGetProperty("order", out _) || !TimestampsHaveOffsets(filters)) return Invalid();
            body = document.RootElement.Deserialize<ExportBody>(Json);
        }
        catch (JsonException) { return Invalid(); }
        if (body?.Filters is null) return Invalid();
        var kind = body.Kind switch
        {
            "events" => HistoryKind.Events,
            "stays" => HistoryKind.Stays,
            "present" => HistoryKind.Present,
            "timeline" => HistoryKind.Timeline,
            _ => (HistoryKind)(-1)
        };
        var result = await sender.Send(new ExportHistoryCommand(kind, body.Filters with { Order = body.Order }), context.RequestAborted);
        if (result.Succeeded) context.Items[HistoryTelemetry.RowsItem] = result.Value!.RowCount;
        return result.Succeeded ? Results.File(result.Value!.Content,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "GarajeMetria-history.xlsx") : Failure(context, result.Error!);
    }
    private static bool UniqueFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) return element.EnumerateArray().All(UniqueFields);
        if (element.ValueKind != JsonValueKind.Object) return true;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return element.EnumerateObject().All(p => names.Add(p.Name) && UniqueFields(p.Value));
    }
    private static bool TimestampsHaveOffsets(JsonElement filters) => new[] { "from", "to" }.All(x =>
        !filters.TryGetProperty(x, out var value) || value.ValueKind == JsonValueKind.Null ||
        value.ValueKind == JsonValueKind.String && HasOffset(value.GetString()));
    private static bool HasOffset(string? value) => value is not null && value.Contains('T') &&
        (value.EndsWith('Z') || value.Length >= 6 && value[^3] == ':' && value[^6] is '+' or '-');
    private static IResult Invalid() => ApiProblems.Create(400, "invalid_input", "Los filtros de consulta no son válidos.");
    private static IResult TooLarge() => ApiProblems.Create(413, "payload_too_large", "Cuerpo demasiado grande.");
    private static IResult Failure(HttpContext context, Failure failure)
    {
        context.Items[HistoryTelemetry.FailureItem] = failure.Code.ToString();
        if (failure.RetryAfterSeconds is { } seconds) context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return failure.Code == FailureCode.InvalidInput ? Invalid() : ApiProblems.FromFailure(failure);
    }
}
