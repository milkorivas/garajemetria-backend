using System.Net;
using System.Security.Cryptography.X509Certificates;
using GarajeMetria.Api.Authentication;
using GarajeMetria.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
var privatePath = Environment.GetEnvironmentVariable("GARAJEMETRIA_PRIVATE_CONFIG");
if (string.IsNullOrWhiteSpace(privatePath) || !Path.IsPathFullyQualified(privatePath) || !File.Exists(privatePath))
    throw new InvalidOperationException("An external private configuration file is required (GARAJEMETRIA_PRIVATE_CONFIG).");
if (ApiConfiguration.IsInside(builder.Environment.ContentRootPath, privatePath))
    throw new InvalidOperationException("Private configuration must be outside the application content root.");
builder.Configuration.AddJsonFile(privatePath, optional: false, reloadOnChange: false);
var authentication = ApiConfiguration.ReadAuthenticationOptions(builder.Configuration);
var http = ApiConfiguration.ReadHttpOptions(builder.Configuration);
if (ApiConfiguration.IsInside(builder.Environment.ContentRootPath, http.DataProtectionKeyPath))
    throw new InvalidOperationException("Data Protection keys must be outside the application content root.");
builder.Services.AddSingleton(authentication);
builder.Services.AddSingleton(http);
builder.Services.AddApplication();
GarajeMetria.Infrastructure.DependencyInjection.AddInfrastructure(builder.Services, builder.Configuration);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    context.HttpContext.Response.Headers.CacheControl = "no-store";
});
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = AuthenticationEndpoints.CsrfHeader;
    o.Cookie.Name = AuthenticationEndpoints.AntiforgeryCookie;
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.Path = "/";
});
var protectionCertificate = X509CertificateLoader.LoadPkcs12FromFile(http.DataProtectionCertificatePath,
    builder.Configuration["Http:DataProtectionCertificatePassword"], X509KeyStorageFlags.EphemeralKeySet);
if (!protectionCertificate.HasPrivateKey) throw new InvalidOperationException("Data Protection requires a private certificate.");
builder.Services.AddSingleton(protectionCertificate);
builder.Services.AddDataProtection().SetApplicationName("GarajeMetria.Api")
    .PersistKeysToFileSystem(new DirectoryInfo(http.DataProtectionKeyPath)).ProtectKeysWithCertificate(protectionCertificate);
builder.Services.AddAuthentication("Bearer").AddScheme<AuthenticationSchemeOptions, SessionBearerHandler>("Bearer", _ => { });
builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(http.AllowedOrigins)
    .AllowCredentials().WithMethods("GET", "POST", "OPTIONS").WithHeaders("Content-Type", "Authorization", AuthenticationEndpoints.CsrfHeader)));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Clear();
    foreach (var proxy in http.TrustedProxies) o.KnownProxies.Add(IPAddress.Parse(proxy));
});
builder.Services.AddOpenApi(o => o.AddOperationTransformer<AuthenticationOpenApi>());
var app = builder.Build();
app.UseExceptionHandler();
if (http.TrustedProxies.Length > 0) app.UseForwardedHeaders();
app.UseHttpsRedirection();
if (http.EnableApiDocumentation)
{
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "GarajeMetría v1"));
    app.MapOpenApi().AllowAnonymous();
}
app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapAuthenticationEndpoints();
app.Run();

public partial class Program;
