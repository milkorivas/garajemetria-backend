using System.Globalization;
using GarajeMetria.Api.Authentication;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Users;
using GarajeMetria.Domain.Authentication;
using MediatR;

namespace GarajeMetria.Api.Users;

public sealed record DefaultWorkshopResponse(string UserId, string? WorkshopId);

public static class UserAdministrationEndpoints
{
    public static void MapUserAdministrationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/users").RequireAuthorization(p => p.RequireRole(UserRoles.Administrator)).WithTags("Users");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("", async (HttpContext context, ISender sender, UserAdministrationOptions options) =>
        {
            var query = context.Request.Query;
            if (query.Keys.Any(x => x is not ("pageNumber" or "pageSize")) || query.Any(x => x.Value.Count != 1) ||
                !Integer(query, "pageNumber", 1, out var page) || !Integer(query, "pageSize", options.DefaultPageSize, out var size))
                return Invalid();
            var result = await sender.Send(new ListLocalUsersQuery(page, size), context.RequestAborted);
            return result.Succeeded ? Results.Ok(result.Value) : Failure(result.Error!);
        }).WithName("ListLocalUsers").WithSummary("Consulta paginada de perfiles locales")
            .Produces<LocalUserPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(500).ProducesProblem(503);

        group.MapGet("/{userId}/default-workshop", async (string userId, HttpContext context, ISender sender) =>
        {
            var result = await sender.Send(new GetDefaultWorkshopQuery(userId), context.RequestAborted);
            if (!result.Succeeded) return Failure(result.Error!);
            context.Response.Headers.ETag = ETag(result.Value!.Version);
            return Results.Ok(new DefaultWorkshopResponse(result.Value.UserId, result.Value.WorkshopId));
        }).WithName("GetDefaultWorkshop").WithSummary("Consulta la asignación y su ETag")
            .Produces<DefaultWorkshopResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(500).ProducesProblem(503);

        group.MapPut("/{userId}/default-workshop", async (string userId, HttpContext context, ISender sender, UserAdministrationOptions options) =>
        {
            var header = context.Request.Headers.IfMatch;
            if (header.Count == 0) return ApiProblems.Create(428, "precondition_required", "Se requiere If-Match");
            if (header.Count != 1 || !TryETag(header[0], out var version)) return Invalid();
            var read = await StrictAssignmentReader.ReadAsync(context.Request, options, context.RequestAborted);
            if (read.Error is not null) return read.Error;
            var body = read.Request!;
            var result = await sender.Send(new SetDefaultWorkshopCommand(userId, body.WorkshopId, body.Reason, version), context.RequestAborted);
            if (!result.Succeeded) return Failure(result.Error!);
            context.Response.Headers.ETag = ETag(result.Value!.Version);
            return Results.NoContent();
        }).WithName("SetDefaultWorkshop").WithSummary("Cambia el taller con auditoría y cierre de sesión")
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(413).ProducesProblem(415)
            .ProducesProblem(428).ProducesProblem(500).ProducesProblem(503);
    }

    public static string ETag(Guid version) => "\"" + version.ToString("N") + "\"";
    public static bool TryETag(string? value, out Guid version)
    {
        version = default;
        return value is { Length: 34 } && value[0] == '"' && value[^1] == '"' &&
            Guid.TryParseExact(value.AsSpan(1, 32), "N", out version) && version != Guid.Empty && ETag(version) == value;
    }

    private static bool Integer(IQueryCollection query, string name, int fallback, out int value)
    {
        value = fallback;
        return !query.ContainsKey(name) || int.TryParse(query[name], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static IResult Invalid() => ApiProblems.Create(400, "invalid_input", "La solicitud administrativa no es válida");
    private static IResult Failure(Failure failure) => failure.Code == FailureCode.InvalidInput ? Invalid() : ApiProblems.FromFailure(failure);
}
