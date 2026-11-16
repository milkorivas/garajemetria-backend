using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GarajeMetria.Api.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Domain.Lpr;
using MediatR;
using Microsoft.Net.Http.Headers;

namespace GarajeMetria.Api.Lpr;

public sealed record LprEventResponse([property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("received_at")] string ReceivedAt, bool Duplicate,
    [property: JsonPropertyName("plate_status")] string PlateStatus);

public static class LprEventEndpoints
{
    private static readonly string[] Required = ["event_id", "capture_timestamp", "device_id", "site_id", "plate", "confidence"];
    private static readonly Regex Timestamp = new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-5][0-9])\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static void MapLprEventEndpoints(this WebApplication app) => app.MapPost("/api/lpr/events", async (HttpContext context, ISender sender) =>
    {
        var read = await ReadAsync(context.Request, context.RequestAborted);
        if (read.Error is not null) return read.Error;
        var result = await sender.Send(read.Command!, context.RequestAborted);
        if (!result.Succeeded)
        {
            if (result.Error!.RetryAfterSeconds is { } seconds) context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return LprProblems.Failure(result.Error);
        }
        var value = result.Value!;
        var response = new LprEventResponse(value.Receipt.EventId, value.Receipt.ReceivedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            value.Duplicate, value.Receipt.PlateStatus);
        context.Items[LprTelemetry.ReceiptItem] = response;
        return Results.Json(response, statusCode: value.Duplicate ? 200 : 201);
    }).RequireAuthorization(LprOAuth.WriterPolicy).WithMetadata(new LprTrafficMetadata(LprTraffic.Event))
        .WithName("ReceiveLprEvent").WithTags("LPR Events").WithSummary("Recibir una observación LPR de un dispositivo")
        .Produces<LprEventResponse>(201).Produces<LprEventResponse>(200).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
        .ProducesProblem(409).ProducesProblem(413).ProducesProblem(415).ProducesProblem(422).ProducesProblem(429).ProducesProblem(500).ProducesProblem(503);

    private static async Task<(ReceiveLprEventCommand? Command, IResult? Error)> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) ||
            !type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || type.Parameters.Count > 1 ||
            type.Parameters.Any(x => !x.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)) ||
            type.Charset.HasValue && !type.Charset.Value!.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
            request.Headers.ContentEncoding.Count > 1 || request.Headers.ContentEncoding.Count == 1 && request.Headers.ContentEncoding[0] != "identity")
            return (null, ApiProblems.Create(415, "unsupported_media_type", "Se requiere JSON UTF-8."));
        if (request.ContentLength > 8192) return TooLarge();
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await request.Body.ReadAsync(buffer, ct)) != 0)
        {
            if (stream.Length + count > 8192) return TooLarge();
            await stream.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        try
        {
            var bytes = stream.ToArray();
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || request.Query.Count != 0) return Invalid();
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in json.RootElement.EnumerateObject())
                if ((!Required.Contains(property.Name, StringComparer.Ordinal) && property.Name != "direction") || !fields.TryAdd(property.Name, property.Value)) return Invalid();
            if (Required.Any(x => !fields.ContainsKey(x)) ||
                fields["event_id"].GetString() is not { Length: 36 } eventText || !Guid.TryParseExact(eventText, "D", out var eventId) ||
                fields["device_id"].GetString() is not { Length: 36 } deviceText || !Guid.TryParseExact(deviceText, "D", out var deviceId) ||
                fields["capture_timestamp"].GetString() is not { } timestamp || !Timestamp.IsMatch(timestamp) || timestamp.EndsWith("-00:00", StringComparison.Ordinal) ||
                !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var capture) ||
                fields["site_id"].GetString() is not { } site || fields["plate"].GetString() is not { } plate ||
                fields["confidence"].ValueKind != JsonValueKind.Number || !ExactConfidence(fields["confidence"].GetRawText(), out var confidence)) return Invalid();
            LprDirection? direction = null;
            if (fields.TryGetValue("direction", out var supplied) && supplied.ValueKind != JsonValueKind.Null)
                switch (supplied.GetString())
                {
                    case "entry": direction = LprDirection.Entry; break;
                    case "exit": direction = LprDirection.Exit; break;
                    default: return Invalid();
                }
            return (new(eventId, deviceId, site, capture, plate, confidence, direction), null);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException or ArgumentException or FormatException)
        { return Invalid(); }
    }

    // JsonElement.GetDecimal may round excessive significant digits. Normalize the JSON
    // number lexically first so conversion cannot silently change the observation.
    private static bool ExactConfidence(string raw, out decimal value)
    {
        value = 0;
        var exponentAt = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentAt < 0 ? raw : raw[..exponentAt];
        var point = mantissa.IndexOf('.');
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('-').TrimStart('0');
        if (digits.Length == 0) return true;
        if (raw[0] == '-') return false;
        var zeros = digits.Length - digits.TrimEnd('0').Length;
        digits = digits.TrimEnd('0');
        var power = (exponentAt < 0 ? BigInteger.Zero : BigInteger.Parse(raw[(exponentAt + 1)..], CultureInfo.InvariantCulture)) -
            (point < 0 ? 0 : mantissa.Length - point - 1) + zeros;
        if (power < -8 || power > 0 || digits.Length > 9) return false;
        return decimal.TryParse(digits + "e" + power.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value <= 1;
    }
    private static (ReceiveLprEventCommand?, IResult?) Invalid() => (null, LprProblems.Invalid());
    private static (ReceiveLprEventCommand?, IResult?) TooLarge() => (null, ApiProblems.Create(413, "payload_too_large", "Cuerpo demasiado grande."));
}
