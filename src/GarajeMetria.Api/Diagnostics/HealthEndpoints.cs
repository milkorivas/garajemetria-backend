using GarajeMetria.Api.Authentication;
using GarajeMetria.Application.Diagnostics;
using MediatR;
using Microsoft.AspNetCore.RateLimiting;

namespace GarajeMetria.Api.Diagnostics;

public sealed record HealthModuleResponse(string Status);
public sealed record HealthModulesResponse(HealthModuleResponse Service, HealthModuleResponse Sql, HealthModuleResponse ActiveDirectory);
public sealed record HealthResponse(string Status, HealthModulesResponse Modules);
public sealed class HealthEndpointMetadata;

public static class HealthEndpoints
{
    public static void AddHealthDiagnostics(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddFixedWindowLimiter("health", limiter =>
            {
                limiter.PermitLimit = 60;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                await ApiProblems.FromFailure(new(GarajeMetria.Application.Authentication.FailureCode.RateLimited))
                    .ExecuteAsync(context.HttpContext);
            };
        });
    }

    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", async (HttpContext context, ISender sender, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var result = await sender.Send(new HealthQuery(), ct);
            var response = new HealthResponse(result.Healthy ? "OK" : "ERROR",
                new(new("OK"), new(StatusName(result.Sql)), new(StatusName(result.ActiveDirectory))));
            return Results.Json(response, statusCode: result.Healthy ? 200 : 503);
        }).AllowAnonymous().WithMetadata(new HealthEndpointMetadata()).RequireRateLimiting("health")
            .WithTags("Diagnostics").WithSummary("Comprobar el servicio, SQL Server y Active Directory")
            .WithDescription("Diagnóstico anónimo: OK, ERROR o NOT_CONFIGURED por módulo. SQL ejecuta SELECT 1; AD realiza conexión LDAPS y bind de servicio. Máximo cinco segundos por módulo y 60 peticiones/minuto por instancia.")
            .Produces<HealthResponse>().Produces<HealthResponse>(503).ProducesProblem(429).ProducesProblem(500);
    }

    private static string StatusName(ModuleStatus status) => status switch
    {
        ModuleStatus.Ok => "OK",
        ModuleStatus.NotConfigured => "NOT_CONFIGURED",
        _ => "ERROR"
    };
}
