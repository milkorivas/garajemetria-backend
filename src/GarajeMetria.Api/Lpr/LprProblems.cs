using GarajeMetria.Api.Authentication;
using GarajeMetria.Application.Authentication;

namespace GarajeMetria.Api.Lpr;

public static class LprProblems
{
    public static IResult Invalid() => ApiProblems.Create(400, "invalid_input", "La solicitud LPR no es válida.");
    public static IResult Failure(Failure failure) => failure.Code switch
    {
        FailureCode.InvalidInput => Invalid(),
        FailureCode.ResourceNotFound => ApiProblems.Create(404, "resource_not_found", "No se encuentra el dispositivo."),
        FailureCode.DeviceConflict => ApiProblems.Create(409, "device_conflict", "El estado del dispositivo o taller no permite esta operación."),
        FailureCode.EventConflict => ApiProblems.Create(409, "event_conflict", "El identificador del evento ya existe con otros datos."),
        FailureCode.CaptureOutsideWindow => ApiProblems.Create(422, "capture_outside_window", "La captura está fuera de la ventana admitida."),
        FailureCode.PreconditionFailed => ApiProblems.Create(412, "precondition_failed", "El recurso ha cambiado. Obtén su ETag actual."),
        FailureCode.InvalidDeviceToken => ApiProblems.Create(401, "invalid_device_token", "El token de dispositivo no es válido."),
        FailureCode.DeviceForbidden => ApiProblems.Create(403, "device_forbidden", "El dispositivo no tiene permiso para esta operación."),
        FailureCode.InactiveWorkshop => ApiProblems.Create(403, "workshop_inactive", "El taller no está activo."),
        FailureCode.OperationOutcomeUnknown => ApiProblems.Create(500, "operation_outcome_unknown", "No se pudo confirmar el resultado de la operación."),
        FailureCode.RateLimited => ApiProblems.Create(429, "rate_limited", "Demasiadas solicitudes."),
        _ => ApiProblems.FromFailure(failure)
    };

    public static bool IsTokenRequest(HttpContext context) => context.Request.Path.Equals("/connect/token", StringComparison.OrdinalIgnoreCase);
    public static Task OAuthErrorAsync(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        if (error == "invalid_client") context.Response.Headers.WWWAuthenticate = "Basic";
        return context.Response.WriteAsJsonAsync(new { error }, context.RequestAborted);
    }
}

public sealed class LprFailureMiddleware(RequestDelegate next, ILogger<LprFailureMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<LprTrafficMetadata>();
        if (metadata is null && !LprProblems.IsTokenRequest(context)) { await next(context); return; }
        context.Response.Headers.CacheControl = "no-store";
        try { await next(context); }
        catch (Exception ex) when (ex is not OperationCanceledException && !context.Response.HasStarted)
        {
            var code = ex is PersistenceFailureException persistence ? persistence.Code : FailureCode.InternalError;
            logger.LogWarning("LPR request failed: category={Category}, exceptionType={ExceptionType}", code, ex.GetType().Name);
            if (LprProblems.IsTokenRequest(context))
                await LprProblems.OAuthErrorAsync(context, code == FailureCode.ServiceUnavailable ? 503 : 500,
                    code == FailureCode.ServiceUnavailable ? "temporarily_unavailable" : "server_error");
            else await LprProblems.Failure(new(code)).ExecuteAsync(context);
        }
    }
}
