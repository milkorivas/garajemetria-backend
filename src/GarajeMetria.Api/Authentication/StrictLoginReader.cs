using System.Text;
using System.Text.Json;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Diagnostics;
using Microsoft.Net.Http.Headers;

namespace GarajeMetria.Api.Authentication;

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginReadResult(LoginRequest? Request, IResult? Error);

public static class StrictLoginReader
{
    public static async Task<LoginReadResult> ReadAsync(HttpRequest request, AuthenticationOptions options, ILogger logger, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            contentType.Parameters.Any(p => !p.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)) ||
            contentType.Parameters.Count > 1 || contentType.Charset.HasValue && !contentType.Charset.Value!.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
            request.Headers.ContentEncoding.Count > 1 || request.Headers.ContentEncoding.Count == 1 &&
                !string.Equals(request.Headers.ContentEncoding[0], "identity", StringComparison.OrdinalIgnoreCase))
            return new(null, ApiProblems.Create(415, "unsupported_media_type", "El formato del contenido no está admitido"));
        if (request.ContentLength > options.MaxLoginBodyBytes) return TooLarge();
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (stream.Length + read > options.MaxLoginBodyBytes) return TooLarge();
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        try
        {
            var bytes = stream.ToArray();
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = options.MaxJsonDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Invalid();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is not ("username" or "password") || property.Value.ValueKind != JsonValueKind.String ||
                    !values.TryAdd(property.Name, property.Value.GetString()!)) return Invalid();
            }
            return values.Count == 2 ? new(new(values["username"], values["password"]), null) : Invalid();
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException or ArgumentException)
        {
            logger.LogSafeException(LogLevel.Information, "Login.InvalidPayload", ex);
            return Invalid();
        }
    }

    private static LoginReadResult Invalid() => new(null, ApiProblems.FromFailure(new(FailureCode.InvalidInput)));
    private static LoginReadResult TooLarge() => new(null, ApiProblems.Create(413, "payload_too_large", "El cuerpo de la solicitud supera el tamaño permitido"));
}
