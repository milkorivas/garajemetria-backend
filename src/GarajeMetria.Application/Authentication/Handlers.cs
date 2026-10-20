using FluentValidation;
using GarajeMetria.Domain.Authentication;
using MediatR;

namespace GarajeMetria.Application.Authentication;

public sealed record LoginCommand(string Username, string Password) : IRequest<Outcome<AuthenticationResult>>
{
    public override string ToString() => nameof(LoginCommand);
}
public sealed record RefreshSessionCommand(string Token) : IRequest<Outcome<AuthenticationResult>>
{
    public override string ToString() => nameof(RefreshSessionCommand);
}
public sealed record LogoutCommand(string Token) : IRequest<Outcome<bool>>
{
    public override string ToString() => nameof(LogoutCommand);
}

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator(AuthenticationOptions options)
    {
        RuleFor(x => x.Username).Must(v => v is not null && InputRules.ValidUsername(v, options.MaxUsernameLength))
            .WithMessage("Invalid username format or length.");
        RuleFor(x => x.Password).Must(v => v is not null && InputRules.ScalarCount(v) is var n && n > 0 &&
            n <= options.MaxPasswordLength && !v.Contains('\0')).WithMessage("Invalid password format or length.");
    }
}

public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken);
            if (!result.IsValid)
            {
                if (typeof(TResponse) == typeof(Outcome<AuthenticationResult>))
                    return (TResponse)(object)Outcome<AuthenticationResult>.Fail(FailureCode.InvalidInput);
                throw new InvalidOperationException("No validation result mapping is registered for this request.");
            }
        }
        return await next();
    }
}

public sealed class LoginHandler(ICorporateDirectory directory, IUserAccessQuery users,
    IAuthenticationSessionRepository sessions, IUnitOfWork unitOfWork, ITokenService tokens,
    IAuthenticationRateLimitStore limits, AuthenticationOptions options, TimeProvider clock)
    : IRequestHandler<LoginCommand, Outcome<AuthenticationResult>>
{
    public async Task<Outcome<AuthenticationResult>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var (domain, username) = InputRules.Resolve(request.Username, options.DefaultDomain);
        var permit = await limits.AdmitAsync(options.Policy("login-user"), InputRules.Normalize(domain + "\\" + username), cancellationToken);
        if (!permit.Allowed) return Outcome<AuthenticationResult>.Fail(FailureCode.RateLimited, permit.RetryAfterSeconds);
        if (!options.AllowedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            return Outcome<AuthenticationResult>.Fail(FailureCode.InvalidCredentials);

        var result = await directory.AuthenticateAsync(username, request.Password, cancellationToken);
        if (result.Status == DirectoryStatus.Unavailable) return Outcome<AuthenticationResult>.Fail(FailureCode.DirectoryUnavailable);
        if (result.Status != DirectoryStatus.Valid || result.Identity is null) return Outcome<AuthenticationResult>.Fail(FailureCode.InvalidCredentials);
        var role = result.Identity.Role;
        if (role is null) return Outcome<AuthenticationResult>.Fail(FailureCode.NoRole);
        var user = await users.ProvisionAsync(result.Identity, cancellationToken);
        if (AccessDecision.Check(user, role) is { } rejected) return Outcome<AuthenticationResult>.Fail(rejected);

        return await unitOfWork.ExecuteAsync(user.Id, async ct =>
        {
            var current = await users.FindAsync(user.Id, ct);
            if (current is null) return Outcome<AuthenticationResult>.Fail(FailureCode.SessionEnded);
            if (AccessDecision.Check(current, role) is { } failure) return Outcome<AuthenticationResult>.Fail(failure);
            var now = clock.GetUtcNow();
            var session = new AuthenticationSession(Guid.NewGuid(), user.Id, role, now,
                now.AddSeconds(options.MaxSessionLifetimeSeconds));
            var pair = tokens.Issue(session);
            if (pair is null || pair.Access.ExpiresAt <= clock.GetUtcNow() || pair.Refresh.ExpiresAt <= clock.GetUtcNow())
                return Outcome<AuthenticationResult>.Fail(FailureCode.SessionEnded);
            var previous = await sessions.FindOpenAsync(user.Id, ct);
            previous?.Close(now, "replaced_by_login");
            sessions.Add(session);
            sessions.Add(new RefreshTokenRecord(pair.Refresh.Id, session.Id, pair.Refresh.Hash, pair.Refresh.IssuedAt, pair.Refresh.ExpiresAt));
            return Outcome<AuthenticationResult>.Success(new(pair, new(user.Id.ToString(), current.DisplayName, role, current.DefaultWorkshopId)));
        }, cancellationToken);
    }
}

public sealed class RefreshSessionHandler(ICorporateDirectory directory, IUserAccessQuery users,
    IAuthenticationSessionRepository sessions, IUnitOfWork unitOfWork, ITokenService tokens,
    IAuthenticationRateLimitStore limits, AuthenticationOptions options, TimeProvider clock)
    : IRequestHandler<RefreshSessionCommand, Outcome<AuthenticationResult>>
{
    public async Task<Outcome<AuthenticationResult>> Handle(RefreshSessionCommand request, CancellationToken cancellationToken)
    {
        var identity = tokens.Validate(request.Token, "refresh");
        if (identity is null) return Outcome<AuthenticationResult>.Fail(FailureCode.SessionEnded);
        var initialSession = await sessions.FindAsync(identity.SessionId, cancellationToken);
        var initialToken = await sessions.FindRefreshAsync(identity.TokenId, cancellationToken);
        if (!Matches(initialSession, initialToken, identity, tokens.Hash(request.Token)) || !initialSession!.IsActive(clock.GetUtcNow()))
            return Outcome<AuthenticationResult>.Fail(FailureCode.SessionEnded);
        var permit = await limits.AdmitAsync(options.Policy("refresh-user"), identity.UserId.ToString(), cancellationToken);
        if (!permit.Allowed) return Outcome<AuthenticationResult>.Fail(FailureCode.RateLimited, permit.RetryAfterSeconds);

        // Detect replay before consulting AD; the locked decision is repeated after any external call.
        if (initialToken!.Inspect(initialSession, clock.GetUtcNow()) is RefreshUse.Expired or RefreshUse.Invalid)
            return Outcome<AuthenticationResult>.Fail(FailureCode.SessionEnded);
        if (initialToken!.Inspect(initialSession, clock.GetUtcNow()) == RefreshUse.Reused)
            return await CompleteAsync(identity, request.Token, null, cancellationToken);
        var user = await users.FindAsync(identity.UserId, cancellationToken);
        if (user is null) return Outcome<AuthenticationResult>.Fail(FailureCode.SessionEnded);
        var state = await directory.ReadAsync(user.DirectoryObjectId, cancellationToken);
        if (state.Status == DirectoryStatus.Unavailable) return Outcome<AuthenticationResult>.Fail(FailureCode.DirectoryUnavailable);
        return await CompleteAsync(identity, request.Token, state, cancellationToken);
    }

    private Task<Outcome<AuthenticationResult>> CompleteAsync(TokenIdentity identity, string rawToken, DirectoryResult? state,
        CancellationToken cancellationToken) => unitOfWork.ExecuteAsync(identity.UserId, async ct =>
    {
        var session = await sessions.FindAsync(identity.SessionId, ct);
        var record = await sessions.FindRefreshAsync(identity.TokenId, ct);
        if (!Matches(session, record, identity, tokens.Hash(rawToken))) return Outcome<AuthenticationResult>.Fail(FailureCode.SessionEnded);
        var now = clock.GetUtcNow();
        var use = record!.Inspect(session!, now);
        if (use == RefreshUse.Reused)
        {
            session!.Close(now, "refresh_reuse");
            return Outcome<AuthenticationResult>.Fail(FailureCode.SessionEnded);
        }
        if (use != RefreshUse.Accepted) return Outcome<AuthenticationResult>.Fail(FailureCode.SessionEnded);
        if (state is null) return Outcome<AuthenticationResult>.Fail(FailureCode.SessionEnded);
        if (state.Status != DirectoryStatus.Valid || state.Identity?.Role != session!.Role || state.Identity.ObjectId !=
            (await users.FindAsync(identity.UserId, ct))?.DirectoryObjectId)
        {
            session!.Close(now, "directory_access_changed");
            return Outcome<AuthenticationResult>.Fail(FailureCode.SessionEnded);
        }
        var user = await users.FindAsync(identity.UserId, ct);
        if (user is null) return Outcome<AuthenticationResult>.Fail(FailureCode.SessionEnded);
        if (AccessDecision.Check(user, session.Role) is { } failure)
        {
            if (failure == FailureCode.ServiceUnavailable) return Outcome<AuthenticationResult>.Fail(failure);
            session.Close(now, "workshop_access_changed");
            return Outcome<AuthenticationResult>.Fail(FailureCode.SessionEnded);
        }
        var pair = tokens.Issue(session);
        now = clock.GetUtcNow();
        if (pair is null || now >= pair.Access.ExpiresAt || now >= pair.Refresh.ExpiresAt || now >= record.ExpiresAt)
            return Outcome<AuthenticationResult>.Fail(FailureCode.SessionEnded);
        if (record.Consume(session, now) != RefreshUse.Accepted) return Outcome<AuthenticationResult>.Fail(FailureCode.SessionEnded);
        sessions.Add(new RefreshTokenRecord(pair.Refresh.Id, session.Id, pair.Refresh.Hash, pair.Refresh.IssuedAt, pair.Refresh.ExpiresAt));
        return Outcome<AuthenticationResult>.Success(new(pair, null));
    }, cancellationToken);

    internal static bool Matches(AuthenticationSession? session, RefreshTokenRecord? record, TokenIdentity identity, string hash) =>
        session is not null && record is not null && session.UserId == identity.UserId && session.Id == identity.SessionId &&
        record.SessionId == session.Id && record.Id == identity.TokenId && record.TokenHash == hash &&
        record.IssuedAt == identity.IssuedAt && record.ExpiresAt == identity.ExpiresAt;
}

public sealed class LogoutHandler(IAuthenticationSessionRepository sessions, IUnitOfWork unitOfWork,
    ITokenService tokens, TimeProvider clock) : IRequestHandler<LogoutCommand, Outcome<bool>>
{
    public async Task<Outcome<bool>> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        var identity = tokens.Validate(request.Token, "refresh");
        if (identity is null) return Outcome<bool>.Fail(FailureCode.SessionEnded);
        return await unitOfWork.ExecuteAsync(identity.UserId, async ct =>
        {
            var session = await sessions.FindAsync(identity.SessionId, ct);
            var record = await sessions.FindRefreshAsync(identity.TokenId, ct);
            if (!RefreshSessionHandler.Matches(session, record, identity, tokens.Hash(request.Token))) return Outcome<bool>.Fail(FailureCode.SessionEnded);
            var now = clock.GetUtcNow();
            var use = record!.Inspect(session!, now);
            if (use == RefreshUse.Reused)
            {
                session!.Close(now, "refresh_reuse");
                return Outcome<bool>.Fail(FailureCode.SessionEnded);
            }
            if (use != RefreshUse.Accepted) return Outcome<bool>.Fail(FailureCode.SessionEnded);
            session!.Close(now, "logout");
            return Outcome<bool>.Success(true);
        }, cancellationToken);
    }
}

public sealed record CleanupAuthenticationCommand(string Owner) : IRequest<CleanupResult>;
public sealed class CleanupAuthenticationHandler(IAuthenticationRetentionStore store, AuthenticationOptions options)
    : IRequestHandler<CleanupAuthenticationCommand, CleanupResult>
{
    public Task<CleanupResult> Handle(CleanupAuthenticationCommand request, CancellationToken cancellationToken) =>
        store.CleanBatchAsync(request.Owner, options.Retention, cancellationToken);
}
