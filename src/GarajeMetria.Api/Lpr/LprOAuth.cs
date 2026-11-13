using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using MediatR;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation;
using OpenIddict.Validation.AspNetCore;

namespace GarajeMetria.Api.Lpr;

public static class LprOAuth
{
    public const string DeviceScheme = "LprDevice";
    public const string WriterPolicy = "LprEventWriter";
    public const string Scope = "lpr.events.write";
    internal const string GrantItem = "GarajeMetria.Lpr.Grant";

    public static void AddDeviceOAuth(this IServiceCollection services)
    {
        services.AddScoped<HttpCurrentDeviceContext>();
        services.AddScoped<ICurrentDeviceContext>(sp => sp.GetRequiredService<HttpCurrentDeviceContext>());
        services.ConfigureOptions<LprServerOptions>();
        services.AddOpenIddict().AddServer(options =>
        {
            options.SetTokenEndpointUris("/connect/token");
            options.AllowClientCredentialsFlow();
            options.RegisterScopes(Scope);
            options.UseAspNetCore().EnableTokenEndpointPassthrough();
            options.AddEventHandler<OpenIddictServerEvents.ExtractTokenRequestContext>(b => b.UseScopedHandler<ParsedClientQuota>()
                .SetOrder(OpenIddictServerAspNetCoreHandlers.ExtractBasicAuthenticationCredentials<OpenIddictServerEvents.ExtractTokenRequestContext>.Descriptor.Order + 500));
            options.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(b => b.UseSingletonHandler<PreserveGrantDates>()
                .SetOrder(OpenIddictServerHandlers.PrepareAccessTokenPrincipal.Descriptor.Order + 500));
            options.AddEventHandler<OpenIddictServerEvents.ApplyTokenResponseContext>(b => b.UseScopedHandler<SafeTokenErrors>().SetOrder(int.MinValue + 100));
        }).AddValidation(options =>
        {
            options.UseLocalServer();
            options.EnableTokenEntryValidation();
            options.AddAudiences("garajemetria-lpr");
            options.UseAspNetCore();
        });
        services.AddOptions<OpenIddictValidationOptions>().Configure<TimeProvider>((options, clock) =>
        { options.TimeProvider = clock; options.TokenValidationParameters.ClockSkew = TimeSpan.Zero; });
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, DeviceAuthenticationHandler>(DeviceScheme, _ => { });
        services.AddAuthorizationBuilder().AddPolicy(WriterPolicy, policy => policy.AddAuthenticationSchemes(DeviceScheme).RequireAuthenticatedUser());
    }

    public static void MapDeviceOAuth(this WebApplication app)
    {
        app.MapPost("/connect/token", async (HttpContext context, ISender sender) =>
        {
            var request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("OAuth request is unavailable.");
            var grant = await sender.Send(new PrepareDeviceTokenGrantCommand(request.ClientId!, request.ClientSecret!), context.RequestAborted);
            if (!grant.Succeeded)
            {
                var code = grant.Error!.Code;
                return (IResult)new OAuthFailureResult(code == FailureCode.ServiceUnavailable ? 503 : code is FailureCode.InvalidInput or FailureCode.InvalidCredentials ? 401 : 500,
                    code == FailureCode.ServiceUnavailable ? "temporarily_unavailable" : code is FailureCode.InvalidInput or FailureCode.InvalidCredentials ? "invalid_client" : "server_error");
            }
            var value = grant.Value!;
            context.Items[GrantItem] = value;
            var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType);
            identity.SetClaim(OpenIddictConstants.Claims.Subject, value.DeviceId.ToString("D"));
            identity.SetClaim(OpenIddictConstants.Claims.ClientId, value.ClientId);
            identity.SetClaim("site_id", value.WorkshopId);
            identity.SetClaim("device_auth_version", value.AuthorizationVersion.ToString("D"));
            identity.SetScopes(Scope);
            identity.SetResources("garajemetria-lpr");
            identity.SetDestinations(_ => [OpenIddictConstants.Destinations.AccessToken]);
            return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }).AllowAnonymous().WithMetadata(new LprTrafficMetadata(LprTraffic.Token)).WithTags("LPR OAuth")
            .WithName("IssueLprToken").WithSummary("Obtener un token de dispositivo mediante Client Credentials")
            .Produces<OAuthTokenResponse>().Produces<OAuthErrorResponse>(400).Produces<OAuthErrorResponse>(401)
            .Produces<OAuthErrorResponse>(413).Produces<OAuthErrorResponse>(429).Produces<OAuthErrorResponse>(500).Produces<OAuthErrorResponse>(503);
    }
}

public sealed record OAuthTokenResponse([property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType, [property: JsonPropertyName("expires_in")] int ExpiresIn, string Scope)
{
    public override string ToString() => nameof(OAuthTokenResponse);
}
public sealed record OAuthErrorResponse(string Error);
internal sealed class OAuthFailureResult(int status, string error) : IResult
{
    public Task ExecuteAsync(HttpContext context) => LprProblems.OAuthErrorAsync(context, status, error);
}

public sealed class LprServerOptions([FromKeyedServices("lpr")] SigningCredentials signing,
    [FromKeyedServices("lpr")] EncryptingCredentials encryption, IConfiguration configuration, TimeProvider clock)
    : IConfigureOptions<OpenIddictServerOptions>
{
    public void Configure(OpenIddictServerOptions options)
    {
        options.Issuer = new Uri(configuration["DeviceOAuth:Issuer"]!);
        options.AccessTokenLifetime = TimeSpan.FromMinutes(configuration.GetValue<int>("DeviceOAuth:AccessTokenMinutes", 15));
        options.TimeProvider = clock;
        options.SigningCredentials.Add(signing);
        options.EncryptionCredentials.Add(encryption);
        options.ClientAuthenticationMethods.Clear();
        options.ClientAuthenticationMethods.Add(OpenIddictConstants.ClientAuthenticationMethods.ClientSecretBasic);
        options.ConfigurationEndpointUris.Clear();
        options.JsonWebKeySetEndpointUris.Clear();
        options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
    }
}

public sealed class ParsedClientQuota(ILprRateLimitStore limits, LprOptions options, ILogger<ParsedClientQuota> logger)
    : IOpenIddictServerHandler<OpenIddictServerEvents.ExtractTokenRequestContext>
{
    public async ValueTask HandleAsync(OpenIddictServerEvents.ExtractTokenRequestContext context)
    {
        var http = context.Transaction.GetHttpRequest()!.HttpContext;
        var request = context.Request ?? throw new InvalidOperationException("OAuth request is unavailable.");
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (form.Any(x => x.Value.Count != 1) || form.ContainsKey("client_id") || form.ContainsKey("client_secret") ||
            form.ContainsKey("client_assertion") || http.Request.Headers.Authorization.Count != 1)
        { context.Reject(OpenIddictConstants.Errors.InvalidRequest); return; }
        if (http.Request.Headers.Authorization[0] is not { } header || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        { context.Reject(OpenIddictConstants.Errors.InvalidClient); return; }
        if (request.ClientId is { Length: > 0 } clientId)
        {
            if (!await LprRateLimits.AdmitAsync(http, limits, options, LprTraffic.Token, "lpr:tokens-client", clientId, logger))
            { context.HandleRequest(); return; }
        }
        if (request.GetScopes().Any(x => x != LprOAuth.Scope)) context.Reject(OpenIddictConstants.Errors.InvalidScope);
    }
}

public sealed class PreserveGrantDates : IOpenIddictServerHandler<OpenIddictServerEvents.ProcessSignInContext>
{
    public ValueTask HandleAsync(OpenIddictServerEvents.ProcessSignInContext context)
    {
        if (context.Transaction.GetHttpRequest()?.HttpContext.Items[LprOAuth.GrantItem] is DeviceTokenGrant grant)
        {
            var principal = context.AccessTokenPrincipal ?? throw new InvalidOperationException("Device token principal is unavailable.");
            principal.SetCreationDate(grant.IssuedAt);
            principal.SetExpirationDate(grant.ExpiresAt);
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class SafeTokenErrors(ILprClock clock) : IOpenIddictServerHandler<OpenIddictServerEvents.ApplyTokenResponseContext>
{
    public async ValueTask HandleAsync(OpenIddictServerEvents.ApplyTokenResponseContext context)
    {
        if (context.Response.Error is not null)
        {
            context.Response.ErrorDescription = context.Response.Error == "invalid_client" ? "Client authentication failed." : "The OAuth request could not be completed.";
            context.Response.ErrorUri = null;
        }
        else if (context.Transaction.GetHttpRequest()?.HttpContext is { } http && http.Items[LprOAuth.GrantItem] is DeviceTokenGrant grant)
        {
            // The grant and authorization checks use the shared SQL clock, including when host clocks differ.
            var now = await clock.GetUtcNowAsync(http.RequestAborted);
            context.Response.ExpiresIn = Math.Max(0, (long)Math.Floor((grant.ExpiresAt - now).TotalSeconds));
            context.Response.Scope = LprOAuth.Scope;
        }
    }
}

public sealed class HttpCurrentDeviceContext : ICurrentDeviceContext
{
    public CurrentDevice? Device { get; internal set; }
}

public sealed class DeviceAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, HttpCurrentDeviceContext deviceContext, ISender sender)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private Failure failure = new(FailureCode.InvalidDeviceToken);
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        try
        {
            var validated = await Context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
            if (!validated.Succeeded || validated.Principal is not { } principal) return AuthenticateResult.Fail("Invalid device token.");
            if (!Guid.TryParse(principal.GetClaim(OpenIddictConstants.Claims.Subject), out var id) ||
                !Guid.TryParse(principal.GetClaim("device_auth_version"), out var version) ||
                principal.GetClaim("site_id") is not { } site || principal.GetExpirationDate() is not { } expiry ||
                principal.GetClaim(OpenIddictConstants.Claims.ClientId) != "lpr_" + id.ToString("N")) return AuthenticateResult.Fail("Invalid device token.");
            deviceContext.Device = new(id, site, version, expiry, principal.HasScope(LprOAuth.Scope));
            var access = await sender.Send(new ValidateDeviceAccessQuery(id, site), Context.RequestAborted);
            if (!access.Succeeded)
            {
                failure = access.Error!;
                deviceContext.Device = null;
                return AuthenticateResult.Fail("Device access denied.");
            }
            return AuthenticateResult.Success(new(principal, Scheme.Name));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            deviceContext.Device = null;
            failure = new(ex is PersistenceFailureException persistence ? persistence.Code : FailureCode.InternalError);
            Logger.LogWarning("Device authentication failed: category={Category}, exceptionType={ExceptionType}", failure.Code, ex.GetType().Name);
            return AuthenticateResult.Fail("Device authentication unavailable.");
        }
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (failure.Code == FailureCode.InvalidDeviceToken) Response.Headers.WWWAuthenticate = "Bearer";
        return LprProblems.Failure(failure).ExecuteAsync(Context);
    }
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => LprProblems.Failure(new(FailureCode.DeviceForbidden)).ExecuteAsync(Context);
}

public sealed class DeviceTokenHttpsMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context) => LprProblems.IsTokenRequest(context) && !context.Request.IsHttps
        ? LprProblems.OAuthErrorAsync(context, 400, "invalid_request") : next(context);
}

public sealed class DeviceTokenBodyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!LprProblems.IsTokenRequest(context)) { await next(context); return; }
        if (!HttpMethods.IsPost(context.Request.Method) || context.Request.Query.Count != 0 || !context.Request.HasFormContentType ||
            context.Request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) != true ||
            context.Request.Headers.ContentEncoding.Count != 0)
        { await LprProblems.OAuthErrorAsync(context, 400, "invalid_request"); return; }
        if (context.Request.ContentLength > 4096) { await LprProblems.OAuthErrorAsync(context, 413, "invalid_request"); return; }
        using var buffer = new MemoryStream();
        var bytes = new byte[1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(bytes, context.RequestAborted)) != 0)
        {
            if (buffer.Length + read > 4096) { await LprProblems.OAuthErrorAsync(context, 413, "invalid_request"); return; }
            await buffer.WriteAsync(bytes.AsMemory(0, read), context.RequestAborted);
        }
        buffer.Position = 0;
        var original = context.Request.Body;
        context.Request.Body = buffer;
        try
        {
            try
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                if (form.Any(x => x.Value.Count != 1) || context.Request.Headers.Authorization.Count > 1)
                { await LprProblems.OAuthErrorAsync(context, 400, "invalid_request"); return; }
            }
            catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
            { await LprProblems.OAuthErrorAsync(context, 400, "invalid_request"); return; }
            await next(context);
        }
        finally { context.Request.Body = original; }
    }
}
