using GarajeMetria.Api.Authentication;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Workshops;
using GarajeMetria.Domain.Authentication;
using MediatR;

namespace GarajeMetria.Api.Workshops;

public static class WorkshopEndpoints
{
    public static void MapWorkshopEndpoints(this WebApplication app)
    {
        app.MapGet("/api/workshops", async (HttpContext context, ISender sender, WorkshopCatalogOptions options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var query = context.Request.Query;
            if (query.Keys.Any(x => x is not ("pageNumber" or "pageSize" or "isActive")) || query.Any(x => x.Value.Count != 1) ||
                !TryInteger(query, "pageNumber", 1, out var page) ||
                !TryInteger(query, "pageSize", options.DefaultPageSize, out var size) ||
                query.ContainsKey("isActive") && !bool.TryParse(query["isActive"], out _))
                return ApiProblems.Create(400, "invalid_input", "Los parámetros de consulta no son válidos");
            bool? active = query.ContainsKey("isActive") ? bool.Parse(query["isActive"].ToString()) : null;
            var result = await sender.Send(new ListWorkshopsQuery(page, size, active), context.RequestAborted);
            if (result.Error?.Code == FailureCode.InvalidInput)
                return ApiProblems.Create(400, "invalid_input", "Los parámetros de consulta no son válidos");
            return result.Succeeded ? Results.Ok(result.Value) : ApiProblems.FromFailure(result.Error!);
        }).RequireAuthorization(policy => policy.RequireRole(UserRoles.Administrator))
            .WithName("ListWorkshops").WithTags("Workshops")
            .WithSummary("Consulta paginada del catálogo local de talleres")
            .WithDescription("Solo Administrador. pageNumber: 1; pageSize: 20, máximo 100 (configurables). isActive opcional. Orden por id ascendente.")
            .Produces<WorkshopPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(500).ProducesProblem(503);
    }

    private static bool TryInteger(IQueryCollection query, string name, int fallback, out int value)
    {
        value = fallback;
        return !query.ContainsKey(name) || int.TryParse(query[name], out value);
    }
}
