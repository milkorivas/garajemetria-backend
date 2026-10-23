using System.Security.Claims;
using System.Text.Encodings.Web;
using GarajeMetria.Application.Authentication;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace GarajeMetria.Api.Authentication;

public sealed class SessionBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, ITokenService tokens, ISender sender)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Auth endpoints always share the anonymous antiforgery context.
        if (Context.GetEndpoint()?.Metadata.GetMetadata<AuthEndpointMetadata>() is not null ||
            Context.GetEndpoint()?.Metadata.GetMetadata<GarajeMetria.Api.Diagnostics.HealthEndpointMetadata>() is not null)
            return AuthenticateResult.NoResult();
        var header = Request.Headers.Authorization;
        if (header.Count == 0) return AuthenticateResult.NoResult();
        if (header.Count != 1 || header[0] is not { } value || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("Invalid authentication.");
        var identity = tokens.Validate(value[7..], "access");
        if (identity is null || !await sender.Send(new SessionActiveQuery(identity), Context.RequestAborted)) return AuthenticateResult.Fail("Invalid session.");
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", identity.UserId.ToString()), new Claim("sid", identity.SessionId.ToString()),
            new Claim("role", identity.Role!)], Scheme.Name, "sub", "role"));
        return AuthenticateResult.Success(new(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return ApiProblems.WriteAsync(Context, new(FailureCode.SessionEnded));
    }
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiProblems.Create(403, "forbidden", "No tienes permiso para realizar esta operación").ExecuteAsync(Context);
}

public sealed class AuthEndpointMetadata;
