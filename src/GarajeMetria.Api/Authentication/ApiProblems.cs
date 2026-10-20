using GarajeMetria.Application.Authentication;
using Microsoft.AspNetCore.Diagnostics;

namespace GarajeMetria.Api.Authentication;

public static class ApiProblems
{
    public static IResult FromFailure(Failure failure) => failure.Code switch
    {
        FailureCode.InvalidInput => Create(400, "invalid_input", "La solicitud de inicio de sesión no es válida"),
        FailureCode.InvalidCredentials => Create(401, "invalid_credentials", "No se pudo iniciar sesión"),
        FailureCode.NoRole => Create(403, "role_required", "No tienes un rol asignado en GarajeMetría"),
        FailureCode.NoWorkshop => Create(403, "workshop_required", "No tienes un taller asignado. Contacta con un administrador"),
        FailureCode.InactiveWorkshop => Create(403, "workshop_inactive", "Tu taller asignado no está activo. Contacta con un administrador"),
        FailureCode.SessionEnded => Create(401, "session_ended", "La sesión ha finalizado. Inicia sesión de nuevo"),
        FailureCode.DirectoryUnavailable => Create(503, "directory_unavailable", "El servicio de autenticación no está disponible temporalmente. Inténtalo de nuevo más tarde"),
        FailureCode.ServiceUnavailable => Create(503, "service_unavailable", "El servicio no está disponible temporalmente. Inténtalo de nuevo más tarde", "Servicio no disponible"),
        FailureCode.OperationOutcomeUnknown => Create(503, "operation_outcome_unknown", "No se pudo confirmar el resultado de la operación. Comprueba su estado antes de volver a intentarlo", "Resultado no confirmado"),
        FailureCode.RateLimited => Create(429, "rate_limit_exceeded", "Demasiados intentos. Inténtalo de nuevo más tarde"),
        _ => Create(500, "internal_error", "Se ha producido un error interno. Contacta con soporte si el problema persiste", "Error interno")
    };

    public static IResult Create(int status, string code, string detail, string? title = null) =>
        Results.Problem(statusCode: status, type: "https://api.garajemetria.com/problems/" + code.Replace('_', '-'),
            title: title, detail: detail, instance: "urn:uuid:" + Guid.NewGuid(), extensions: new Dictionary<string, object?> { ["code"] = code });

    public static Task WriteAsync(HttpContext context, Failure failure) => FromFailure(failure).ExecuteAsync(context);
}

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted) return false;
        var code = exception switch
        {
            PersistenceFailureException persistence => persistence.Code,
            _ => FailureCode.InternalError
        };
        logger.LogWarning("Request failed: category={Category}, trace={TraceId}", code, context.TraceIdentifier);
        await ApiProblems.WriteAsync(context, new(code));
        return true;
    }
}
