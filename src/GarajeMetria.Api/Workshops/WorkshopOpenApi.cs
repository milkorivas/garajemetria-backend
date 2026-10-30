using GarajeMetria.Application.Workshops;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace GarajeMetria.Api.Workshops;

public sealed class WorkshopOpenApi(WorkshopCatalogOptions options) : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.RelativePath != "api/workshops") return Task.CompletedTask;
        operation.Parameters =
        [
            new OpenApiParameter { Name = "pageNumber", In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Default = System.Text.Json.Nodes.JsonValue.Create(1) } },
            new OpenApiParameter { Name = "pageSize", In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = options.MaxPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture), Default = System.Text.Json.Nodes.JsonValue.Create(options.DefaultPageSize) } },
            new OpenApiParameter { Name = "isActive", In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean } }
        ];
        var document = context.Document ?? throw new InvalidOperationException("OpenAPI document is required.");
        document.Components ??= new();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Access token de Administrador; sesión vigente."
        };
        operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
        return Task.CompletedTask;
    }
}
