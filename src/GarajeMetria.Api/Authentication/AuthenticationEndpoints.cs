using System.Globalization;
using System.Net;
using GarajeMetria.Application.Authentication;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;

namespace GarajeMetria.Api.Authentication;

public sealed record AntiforgeryResponse(string RequestToken, string HeaderName);
public sealed record TokenResponse(string AccessToken, string TokenType, string AccessTokenExpiresAt);
public sealed record LoginResponse(string AccessToken, string TokenType, string AccessTokenExpiresAt, AuthenticatedUser User);
public sealed class AuthenticationLogCategory;

public static class AuthenticationEndpoints
{
    public const string RefreshCookie = "__Secure-GarajeMetria.RefreshToken";
    public const string AntiforgeryCookie = "__Host-GarajeMetria.Antiforgery";
    public const string CsrfHeader = "X-CSRF-Token";

    public static void MapAuthenticationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Authentication").AllowAnonymous()
            .WithMetadata(new AuthEndpointMetadata());
        group.AddEndpointFilter<AuthenticationRequestFilter>();
        group.MapGet("/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var pair = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new AntiforgeryResponse(pair.RequestToken!, CsrfHeader));
        }).WithSummary("Obtener protección antiforgery sin abrir sesión")
            .Produces<AntiforgeryResponse>().ProducesProblem(403);

        group.MapPost("/login", async (HttpContext context, ISender sender, AuthenticationOptions options, TimeProvider clock,
            ILogger<AuthenticationLogCategory> logger, CancellationToken ct) =>
        {
            var parsed = await StrictLoginReader.ReadAsync(context.Request, options, ct);
            if (parsed.Error is not null) return parsed.Error;
            var result = await sender.Send(new LoginCommand(parsed.Request!.Username, parsed.Request.Password), ct);
            logger.LogInformation("Authentication login: outcome={Outcome}, trace={TraceId}", result.Error?.Code.ToString() ?? "success", context.TraceIdentifier);
            return Respond(context, result, clock);
        }).WithSummary("Iniciar sesión con credenciales corporativas")
            .WithDescription("Requiere bootstrap antiforgery y cabecera X-CSRF-Token. El refresh se entrega solo en cookie Secure/HttpOnly/SameSite=Strict.")
            .Produces<LoginResponse>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(413).ProducesProblem(415).ProducesProblem(429).ProducesProblem(500).ProducesProblem(503);

        group.MapPost("/refresh", async (HttpContext context, ISender sender, TimeProvider clock,
            ILogger<AuthenticationLogCategory> logger, CancellationToken ct) =>
        {
            if (ReadRefresh(context.Request) is not { } token) return ApiProblems.FromFailure(new(FailureCode.SessionEnded));
            var result = await sender.Send(new RefreshSessionCommand(token), ct);
            logger.LogInformation("Authentication refresh: outcome={Outcome}, trace={TraceId}", result.Error?.Code.ToString() ?? "success", context.TraceIdentifier);
            return Respond(context, result, clock);
        }).WithSummary("Renovar una sesión mediante refresh de un solo uso")
            .Produces<TokenResponse>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(500).ProducesProblem(503);

        group.MapPost("/logout", async (HttpContext context, ISender sender, ILogger<AuthenticationLogCategory> logger, CancellationToken ct) =>
        {
            if (ReadRefresh(context.Request) is not { } token) return ApiProblems.FromFailure(new(FailureCode.SessionEnded));
            var result = await sender.Send(new LogoutCommand(token), ct);
            logger.LogInformation("Authentication logout: outcome={Outcome}, trace={TraceId}", result.Error?.Code.ToString() ?? "success", context.TraceIdentifier);
            if (result.Error is not null) return ApiProblems.FromFailure(result.Error);
            context.Response.Cookies.Delete(RefreshCookie, RefreshCookieOptions());
            context.Response.Cookies.Delete(AntiforgeryCookie, new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
            return Results.NoContent();
        }).WithSummary("Cerrar sesión con refresh vigente, aunque el acceso haya expirado")
            .Produces(204).ProducesProblem(401).ProducesProblem(403).ProducesProblem(500).ProducesProblem(503);
    }

    private static IResult Respond(HttpContext context, Outcome<AuthenticationResult> outcome, TimeProvider clock)
    {
        if (outcome.Error is { } error)
        {
            if (error.RetryAfterSeconds is { } seconds) context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return ApiProblems.FromFailure(error);
        }
        var result = outcome.Value!;
        if (result.Tokens.Access.ExpiresAt <= clock.GetUtcNow() || result.Tokens.Refresh.ExpiresAt <= clock.GetUtcNow())
            return ApiProblems.FromFailure(new(FailureCode.SessionEnded));
        var cookie = RefreshCookieOptions();
        cookie.Expires = result.Tokens.Refresh.ExpiresAt;
        context.Response.Cookies.Append(RefreshCookie, result.Tokens.Refresh.Value, cookie);
        var expiry = result.Tokens.Access.ExpiresAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return result.User is { } user
            ? Results.Ok(new LoginResponse(result.Tokens.Access.Value, "Bearer", expiry, user))
            : Results.Ok(new TokenResponse(result.Tokens.Access.Value, "Bearer", expiry));
    }

    public static CookieOptions RefreshCookieOptions() => new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/api/auth" };
    private static string? ReadRefresh(HttpRequest request) => CookieCount(request, RefreshCookie) == 1 &&
        request.Cookies.TryGetValue(RefreshCookie, out var value) ? value : null;

    public static int CookieCount(HttpRequest request, string name) => request.Headers.Cookie
        .SelectMany(x => (x ?? "").Split(';')).Count(part => part.Trim().StartsWith(name + "=", StringComparison.Ordinal));
}

public sealed class AuthenticationRequestFilter(HttpOptions http, AuthenticationOptions options,
    IAuthenticationRateLimitStore limits, IAntiforgery antiforgery, ILogger<AuthenticationRequestFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        context.Response.Headers.CacheControl = "no-store";
        var request = context.Request;
        if (request.Method == "POST" && request.Path.Value is "/api/auth/login" or "/api/auth/refresh")
        {
            var address = context.Connection.RemoteIpAddress;
            var key = address is null ? "unknown-ip" : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
            var policy = options.Policy(request.Path.Value == "/api/auth/login" ? "login-ip" : "refresh-ip");
            var decision = await limits.AdmitAsync(policy, key, context.RequestAborted);
            if (!decision.Allowed)
            {
                logger.LogInformation("Authentication IP quota rejected: trace={TraceId}", context.TraceIdentifier);
                context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return ApiProblems.FromFailure(new(FailureCode.RateLimited));
            }
        }
        if (!OriginRules.IsAllowed(request, http))
        {
            logger.LogInformation("Authentication origin rejected: trace={TraceId}", context.TraceIdentifier);
            return ApiProblems.Create(403, "origin_not_allowed", "El origen de la solicitud no está permitido");
        }
        if (request.Method == "POST")
        {
            if (request.Headers[AuthenticationEndpoints.CsrfHeader].Count != 1 ||
                AuthenticationEndpoints.CookieCount(request, AuthenticationEndpoints.AntiforgeryCookie) != 1)
                return InvalidAntiforgery();
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return InvalidAntiforgery(); }
        }
        return await next(invocation);
    }

    private static IResult InvalidAntiforgery() => ApiProblems.Create(403, "antiforgery_validation_failed", "No se pudo validar la protección de la solicitud");
}
