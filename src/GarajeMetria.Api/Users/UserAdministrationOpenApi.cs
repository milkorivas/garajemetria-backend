using System.Globalization;
using System.Text.Json.Nodes;
using GarajeMetria.Application.Users;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace GarajeMetria.Api.Users;

public sealed class UserAdministrationOpenApi(UserAdministrationOptions options) : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.RelativePath?.StartsWith("api/users", StringComparison.Ordinal) != true) return Task.CompletedTask;
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
        operation.Parameters ??= [];
        if (context.Description.RelativePath == "api/users")
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "pageNumber",
                In = ParameterLocation.Query,
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Default = JsonValue.Create(1) }
            });
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "pageSize",
                In = ParameterLocation.Query,
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = options.MaxPageSize.ToString(CultureInfo.InvariantCulture), Default = JsonValue.Create(options.DefaultPageSize) }
            });
        }
        else
        {
            if (context.Description.HttpMethod == "PUT")
            {
                // The strict reader owns content negotiation and rejects duplicate/unknown fields.
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new()
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = JsonSchemaType.Object,
                                AdditionalPropertiesAllowed = false,
                                Required = new HashSet<string> { "workshopId", "reason" },
                                Properties = new Dictionary<string, IOpenApiSchema>
                                {
                                    ["workshopId"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null, MinLength = 1, MaxLength = 128 },
                                    ["reason"] = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 500 }
                                }
                            }
                        }
                    }
                };
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "If-Match",
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Un único ETag fuerte obtenido de GET; sin wildcard ni etiquetas débiles.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                });
                operation.Description = $"JSON UTF-8 estricto con workshopId obligatorio (string o null) y reason (1–500 valores escalares Unicode, sin controles). Identificadores: 1–128 valores escalares sin espacios de borde. Máximo {options.MaxBodyBytes} bytes. Solo null para un objetivo Administrador verificado en AD. Un cambio efectivo cierra su sesión; un cambio sin efecto conserva versión y sesión.";
            }
            if (operation.Responses is { } responses && responses.TryGetValue(context.Description.HttpMethod == "PUT" ? "204" : "200", out var value) && value is OpenApiResponse response)
            {
                response.Headers ??= new Dictionary<string, IOpenApiHeader>();
                response.Headers["ETag"] = new OpenApiHeader { Description = "Versión opaca de la asignación", Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
            }
        }
        return Task.CompletedTask;
    }
}
