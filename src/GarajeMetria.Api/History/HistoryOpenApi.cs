using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace GarajeMetria.Api.History;

public sealed class HistoryOpenApi : IOpenApiOperationTransformer
{
    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        if (!context.Description.ActionDescriptor.EndpointMetadata.OfType<HistoryMetadata>().Any()) return;
        var document = context.Document!;
        document.Components ??= new();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["HistoryBearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Sesión RF-001 de Administrador o Asesor; ámbito RF-002."
        };
        operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("HistoryBearer", document)] = [] }];
        operation.Description = "Consulta autorizada, actualización manual y auditoría previa a la entrega. " +
            "Sin caché. Página 50 por defecto, máximo 200, total exacto; fechas en UTC y zona Atlantic/Canary. " +
            "Máximo un año local por búsqueda; no se garantiza una fotografía inmutable entre páginas. " +
            "Excel: máximo 10.000 filas, 422 si se supera; 503 sin datos si no se confirma auditoría.";
        if (operation.OperationId == "ExportHistory")
        {
            operation.Responses!["200"] = new OpenApiResponse
            {
                Description = "Archivo Excel completo, hasta 10.000 filas.",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] =
                    new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } }
                }
            };
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
                            Properties = new Dictionary<string, IOpenApiSchema>
                            {
                                ["kind"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = new[] { "events", "stays", "present", "timeline" }.Select(x => (JsonNode)JsonValue.Create(x)!).ToList() },
                                ["filters"] = new OpenApiSchema { Type = JsonSchemaType.Object, Description = "Los mismos filtros de la consulta, sin pageNumber, pageSize ni order; máximo un año. Hasta 100 IDs por selección explícita." },
                                ["order"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("asc")!, JsonValue.Create("desc")!] }
                            },
                            Required = new HashSet<string> { "kind", "filters" },
                            AdditionalPropertiesAllowed = false
                        }
                    }
                }
            };
            Headers(operation);
            return;
        }
        var responseType = operation.OperationId switch
        {
            "GetLprEvent" => typeof(HistoryEndpoints.HistoryEventResponse),
            "GetStay" => typeof(HistoryEndpoints.HistoryStayDetail),
            "SearchLprEvents" or "ListStayObservations" => typeof(HistoryEndpoints.HistoryListResponse<HistoryEndpoints.HistoryEventResponse>),
            "SearchStays" or "ListPresentVehicles" => typeof(HistoryEndpoints.HistoryListResponse<GarajeMetria.Application.History.HistoryStay>),
            "GetVehicleTimeline" => typeof(HistoryEndpoints.HistoryListResponse<HistoryEndpoints.HistoryTimelineItem>),
            _ => typeof(HistoryEndpoints.HistoryListResponse<GarajeMetria.Application.History.HistorySelector>)
        };
        var responseSchema = await context.GetOrCreateSchemaAsync(responseType, cancellationToken: ct);
        operation.Responses!["200"] = new OpenApiResponse
        {
            Description = "Resultado autorizado y auditado; sin caché.",
            Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = responseSchema } }
        };
        Headers(operation);
        var path = context.Description.RelativePath ?? "";
        var detail = path.Contains('{') && !path.EndsWith("observations", StringComparison.Ordinal);
        var events = operation.OperationId == "SearchLprEvents";
        var stays = operation.OperationId is "SearchStays" or "ListPresentVehicles";
        var timeline = operation.OperationId == "GetVehicleTimeline";
        var selector = operation.OperationId is "ListHistoryDevices" or "ListHistoryLocations";
        operation.Parameters ??= [];
        Add("workshopIds", "Uno o varios talleres; omitir para todos los autorizados. Máximo 100 selecciones explícitas.", array: true);
        if (!detail) { Add("pageNumber", "Página inicial 1; salto directo.", integer: true, min: "1"); Add("pageSize", "50 por defecto, máximo 200.", integer: true, min: "1", max: "200"); Add("order", "Orden determinista; timeline ascendente por defecto.", values: ["asc", "desc"]); }
        if (events || stays || timeline)
        {
            Add("plate", "Matrícula o fragmento literal; sin corregir caracteres. 1..32 caracteres.");
            Add("plateMatch", "Timeline exige exacta.", values: ["exact", "contains"]);
        }
        if (events || operation.OperationId == "SearchStays" || timeline)
        {
            Add("from", "Inicio incluido ISO 8601 con offset o Z; junto con to. No mezclar con fechas locales.");
            Add("to", "Fin exclusivo ISO 8601 con offset o Z.");
            Add("fromDate", "Primer día canario incluido, yyyy-MM-dd; junto con throughDate.");
            Add("throughDate", "Último día canario incluido; período predeterminado día actual.");
        }
        if (events)
        {
            Add("originalText", "Texto original ordinal literal, sensible a mayúsculas/espacios, excluyente con plate.");
            Add("originalMatch", "Coincidencia literal.", values: ["exact", "contains"]);
            Add("timeBasis", "Eje del filtro temporal; orden siempre por captura.", values: ["capture", "receipt"]);
            Add("deviceIds", "Dispositivos autorizados, hasta 100.", array: true);
            Add("locationIds", "Versiones de ubicación histórica, hasta 100.", array: true);
            Add("direction", "Dirección observada o ausente.", values: ["entry", "exit", "unknown"]);
            Add("plateStatus", "Clasificación persistida.", values: ["valid", "invalid"]);
            Add("association", "Asociación vigente.", values: ["all", "assigned", "unassigned"]);
            Add("confidenceMode", "RF-003 actual exige confianza; missing devuelve vacío.", values: ["all", "range", "missing"]);
            Add("confidenceMin", "Límite inclusivo [0,1], hasta 8 decimales, requiere range.");
            Add("confidenceMax", "Límite inclusivo [0,1], hasta 8 decimales, requiere range.");
        }
        if (stays)
        {
            Add("state", "Cerrada es provisional.", values: ["open", "pendingEntry", "closed"]);
            Add("entrySource", "Origen de entrada.", values: ["observed", "inferred", "estimated"]);
            Add("exitSource", "Origen de salida.", values: ["observed", "inferred", "estimated"]);
        }
        if (selector) Add("search", "Fragmento de etiqueta, hasta 256 caracteres.");
        if (operation.OperationId == "ListHistoryLocations") Add("deviceIds", "Dispositivos autorizados, hasta 100.", array: true);
        return;

        void Add(string name, string description, bool array = false, bool integer = false, string? min = null, string? max = null, string[]? values = null)
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = name,
                In = ParameterLocation.Query,
                Description = description,
                Style = ParameterStyle.Form,
                Explode = array,
                Schema = new OpenApiSchema
                {
                    Type = array ? JsonSchemaType.Array : integer ? JsonSchemaType.Integer : JsonSchemaType.String,
                    Items = array ? new OpenApiSchema { Type = JsonSchemaType.String } : null,
                    Minimum = min,
                    Maximum = max,
                    Enum = values?.Select(x => (JsonNode)JsonValue.Create(x)!).ToList()
                }
            });
        }
    }
    private static void Headers(OpenApiOperation operation)
    {
        foreach (var response in operation.Responses!)
        {
            if (response.Value is not OpenApiResponse value) continue;
            value.Headers ??= new Dictionary<string, IOpenApiHeader>();
            value.Headers["Cache-Control"] = new OpenApiHeader
            {
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Default = JsonValue.Create("no-store") },
                Description = "Los resultados y errores no se almacenan en caché."
            };
            if (response.Key is "429" or "503") value.Headers["Retry-After"] = new OpenApiHeader
            {
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1" },
                Description = "Presente cuando se conoce el plazo de reintento."
            };
            if (response.Key == "401") value.Headers["WWW-Authenticate"] = new OpenApiHeader { Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
        }
    }
}
public sealed record HistoryMetadata;
