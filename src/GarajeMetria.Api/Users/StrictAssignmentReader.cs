using System.Text;
using System.Text.Json;
using GarajeMetria.Api.Authentication;
using GarajeMetria.Application.Users;
using Microsoft.Net.Http.Headers;

namespace GarajeMetria.Api.Users;

public sealed record AssignmentRequest(string? WorkshopId, string Reason);
public sealed record AssignmentReadResult(AssignmentRequest? Request, IResult? Error);

public static class StrictAssignmentReader
{
    public static async Task<AssignmentReadResult> ReadAsync(HttpRequest request, UserAdministrationOptions options, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            contentType.Parameters.Any(p => !p.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)) ||
            contentType.Parameters.Count > 1 || contentType.Charset.HasValue && !contentType.Charset.Value!.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
            request.Headers.ContentEncoding.Count > 1 || request.Headers.ContentEncoding.Count == 1 &&
                !string.Equals(request.Headers.ContentEncoding[0], "identity", StringComparison.OrdinalIgnoreCase))
            return new(null, ApiProblems.Create(415, "unsupported_media_type", "El formato del contenido no está admitido"));
        if (request.ContentLength > options.MaxBodyBytes) return TooLarge();
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (stream.Length + read > options.MaxBodyBytes) return TooLarge();
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        try
        {
            var bytes = stream.ToArray();
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 2 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Invalid();
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is not ("workshopId" or "reason") ||
                    property.Value.ValueKind != JsonValueKind.String && !(property.Name == "workshopId" && property.Value.ValueKind == JsonValueKind.Null) ||
                    !values.TryAdd(property.Name, property.Value.GetString())) return Invalid();
            }
            return values.Count == 2 && values["reason"] is { } reason ? new(new(values["workshopId"], reason), null) : Invalid();
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException or ArgumentException)
        {
            return Invalid();
        }
    }

    private static AssignmentReadResult Invalid() => new(null, ApiProblems.Create(400, "invalid_input", "La asignación no es válida"));
    private static AssignmentReadResult TooLarge() => new(null, ApiProblems.Create(413, "payload_too_large", "El cuerpo de la solicitud supera el tamaño permitido"));
}
