using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace GarajeMetria.Api.Lpr;

public sealed class LprOpenApi : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        var name = operation.OperationId;
        if (name is null || name != "IssueLprToken" && !name.Contains("Lpr", StringComparison.Ordinal)) return Task.CompletedTask;
        var document = context.Document!;
        document.Components ??= new();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        if (name == "ReceiveLprEvent")
        {
            document.Components.SecuritySchemes["LprOAuth"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Flows = new OpenApiOAuthFlows
                {
                    ClientCredentials = new OpenApiOAuthFlow
                    {
                        TokenUrl = new Uri("/connect/token", UriKind.Relative),
                        Scopes = new Dictionary<string, string> { [LprOAuth.Scope] = "Enviar observaciones del dispositivo y taller autorizados" }
                    }
                }
            };
            operation.Security = [new() { [new OpenApiSecuritySchemeReference("LprOAuth", document)] = [LprOAuth.Scope] }];
            operation.RequestBody = Body("application/json", new Dictionary<string, IOpenApiSchema>
            {
                ["event_id"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid", Description = "UUID canónico con guiones, variante RFC y versiones 1 a 8; sin Nil/Max." },
                ["device_id"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
                ["site_id"] = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 256 },
                ["capture_timestamp"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time", Description = "RFC 3339 con T/Z mayúsculas o zona explícita; hasta 7 decimales, sin -00:00 ni segundos intercalares." },
                ["plate"] = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 32 },
                ["confidence"] = new OpenApiSchema { Type = JsonSchemaType.Number, Minimum = "0", Maximum = "1", Description = "Decimal exactamente representable con hasta 8 posiciones; sin redondear. Admite notación exponencial equivalente." },
                ["direction"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null, Description = "entry, exit, null o ausente." }
            }, ["event_id", "device_id", "site_id", "capture_timestamp", "plate", "confidence"]);
            operation.Description = "Bearer exclusivo de dispositivo con lpr.events.write. JSON UTF-8 estricto, máximo 8192 bytes y profundidad 4. 201 primera persistencia, 200 duplicado idéntico con recibo original; sin Location. 409 conflicto, 422 captura nueva fuera de ventana. Matrícula inválida y confianza baja se conservan. Respuestas no-store, errores ProblemDetails seguros; 429 incluye Retry-After.";
        }
        else if (name == "IssueLprToken")
        {
            document.Components.SecuritySchemes["LprClientBasic"] = new OpenApiSecurityScheme
            { Type = SecuritySchemeType.Http, Scheme = "basic", Description = "client_id y client_secret del dispositivo; exclusivamente client_secret_basic." };
            document.Components.SecuritySchemes["LprOAuth"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Flows = new OpenApiOAuthFlows
                {
                    ClientCredentials = new OpenApiOAuthFlow
                    {
                        TokenUrl = new Uri("/connect/token", UriKind.Relative),
                        Scopes = new Dictionary<string, string> { [LprOAuth.Scope] = "Enviar observaciones del dispositivo y taller autorizados" }
                    }
                }
            };
            operation.Security = [new() { [new OpenApiSecuritySchemeReference("LprClientBasic", document)] = [] }];
            operation.RequestBody = Body("application/x-www-form-urlencoded", new Dictionary<string, IOpenApiSchema>
            {
                ["grant_type"] = new OpenApiSchema { Type = JsonSchemaType.String, Default = JsonValue.Create("client_credentials"), Description = "Único grant admitido: client_credentials." },
                ["scope"] = new OpenApiSchema { Type = JsonSchemaType.String, Default = JsonValue.Create(LprOAuth.Scope), Description = "Solo lpr.events.write; se asigna por defecto si se omite." }
            }, ["grant_type"]);
            operation.Description = "HTTPS obligatorio. Formulario UTF-8 de hasta 4096 bytes, credenciales solo en Authorization Basic; sin cookies ni antiforgery. No refresh token. 429/503 usan error OAuth temporarily_unavailable y 429 incluye Retry-After. Sin discovery/JWKS públicos en esta versión.";
        }
        else
        {
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" };
            operation.Security = [new() { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
            operation.Parameters ??= [];
            if (name == "ListLprDevices")
            {
                operation.Parameters.Add(Parameter("pageNumber", JsonSchemaType.Integer, "1", null));
                operation.Parameters.Add(Parameter("pageSize", JsonSchemaType.Integer, "1", "100"));
                operation.Parameters.Add(Parameter("workshopId", JsonSchemaType.String));
                operation.Parameters.Add(Parameter("isActive", JsonSchemaType.Boolean));
            }
            if (name is "RegisterLprDevice" or "RenameLprDevice")
            {
                var properties = new Dictionary<string, IOpenApiSchema> { ["name"] = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 128 } };
                if (name == "RegisterLprDevice") properties["workshopId"] = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 256 };
                operation.RequestBody = Body("application/json", properties, properties.Keys.ToHashSet());
            }
            if (name is not ("ListLprDevices" or "GetLprDevice" or "RegisterLprDevice"))
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "If-Match",
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Un único ETag fuerte, UUID N entre comillas. Falta: 428, sintaxis inválida: 400, obsoleto: 412.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                });
            operation.Description = "Sesión Bearer de Administrador. JSON UTF-8 estricto, hasta 8192 bytes y profundidad 4; sin propiedades desconocidas o repetidas. Credenciales solo en alta/reactivación/rotación. Sin borrado ni cambio de taller. Respuestas no-store; no acepta cookies como autenticación.";
            if (name != "ListLprDevices" && operation.Responses is { } responses)
                foreach (var pair in responses.Where(x => x.Key is "200" or "201" or "204"))
                    if (pair.Value is OpenApiResponse response)
                    {
                        response.Headers ??= new Dictionary<string, IOpenApiHeader>();
                        response.Headers["ETag"] = new OpenApiHeader { Schema = new OpenApiSchema { Type = JsonSchemaType.String }, Description = "Versión opaca del dispositivo." };
                    }
        }
        return Task.CompletedTask;
    }
    private static OpenApiRequestBody Body(string media, Dictionary<string, IOpenApiSchema> properties, HashSet<string> required) => new()
    {
        Required = true,
        Content = new Dictionary<string, OpenApiMediaType>
        {
            [media] = new()
            {
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = properties,
                    Required = required,
                    AdditionalPropertiesAllowed = media == "application/x-www-form-urlencoded"
                }
            }
        }
    };
    private static OpenApiParameter Parameter(string name, JsonSchemaType type, string? min = null, string? max = null) => new()
    { Name = name, In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = type, Minimum = min, Maximum = max } };
}
