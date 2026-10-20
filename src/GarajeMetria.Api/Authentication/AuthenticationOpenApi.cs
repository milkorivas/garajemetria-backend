using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace GarajeMetria.Api.Authentication;

public sealed class AuthenticationOpenApi(GarajeMetria.Application.Authentication.AuthenticationOptions options) : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<AuthEndpointMetadata>().Any())
        {
            if (context.Description.RelativePath == "api/auth/login")
            {
                // Document the schema without routing-level consumes metadata: the strict reader owns 415.
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
                                Required = new HashSet<string> { "username", "password" },
                                Properties = new Dictionary<string, IOpenApiSchema>
                                {
                                    ["username"] = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = options.MaxUsernameLength },
                                    ["password"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "password", MinLength = 1, MaxLength = options.MaxPasswordLength }
                                }
                            }
                        }
                    }
                };
            }
            if (context.Description.HttpMethod == "POST")
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = AuthenticationEndpoints.CsrfHeader,
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "requestToken del bootstrap /api/auth/antiforgery; requiere su cookie correspondiente.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                });
                operation.Description = (operation.Description ?? "") + " Requiere la pareja antiforgery. Los rechazos no modifican cookies. Refresh/logout reciben el JWT solo mediante la cookie __Secure-GarajeMetria.RefreshToken.";
            }
            operation.Security = [];
        }
        return Task.CompletedTask;
    }
}
