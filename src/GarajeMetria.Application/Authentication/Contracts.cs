using GarajeMetria.Domain.Authentication;

namespace GarajeMetria.Application.Authentication;

public enum FailureCode
{
    InvalidInput, InvalidCredentials, NoRole, NoWorkshop, InactiveWorkshop, SessionEnded,
    DirectoryUnavailable, ServiceUnavailable, InternalError, OperationOutcomeUnknown, RateLimited
}

public sealed record Failure(FailureCode Code, int? RetryAfterSeconds = null);
public interface IOutcome
{
    bool Succeeded { get; }
    Failure? Error { get; }
}

public sealed record Outcome<T>(T? Value, Failure? Error) : IOutcome
{
    public bool Succeeded => Error is null;
    public static Outcome<T> Success(T value) => new(value, null);
    public static Outcome<T> Fail(FailureCode code, int? retryAfter = null) => new(default, new(code, retryAfter));
}

public enum DirectoryStatus { Valid, Rejected, NotFound, Unavailable }
public sealed record DirectoryIdentity(Guid ObjectId, string Username, string DisplayName, bool IsAdministrator, bool IsAdvisor)
{
    public string? Role => UserRoles.Resolve(IsAdministrator, IsAdvisor);
}
public sealed record DirectoryResult(DirectoryStatus Status, DirectoryIdentity? Identity = null);
public interface ICorporateDirectory
{
    Task<DirectoryResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken);
    Task<DirectoryResult> ReadAsync(Guid objectId, CancellationToken cancellationToken);
}

public sealed record LocalIdentity(Guid Id, Guid DirectoryObjectId, string Username, string DisplayName,
    string? DefaultWorkshopId, bool CatalogAvailable, bool WorkshopActive);
public static class AccessDecision
{
    public static FailureCode? Check(LocalIdentity identity, string role) => UserAccessPolicy.Evaluate(role,
        identity.DefaultWorkshopId is not null, identity.CatalogAvailable, identity.WorkshopActive) switch
    {
        WorkshopAccessDecision.Allowed => null,
        WorkshopAccessDecision.WorkshopRequired => FailureCode.NoWorkshop,
        WorkshopAccessDecision.CatalogUnavailable => FailureCode.ServiceUnavailable,
        WorkshopAccessDecision.WorkshopInactive => FailureCode.InactiveWorkshop,
        _ => throw new InvalidOperationException("Unknown access decision.")
    };
}

public interface IUserAccessQuery
{
    Task<LocalIdentity?> FindAsync(Guid userId, CancellationToken cancellationToken);
    Task<LocalIdentity> ProvisionAsync(DirectoryIdentity identity, CancellationToken cancellationToken);
}

public interface IAuthenticationSessionRepository
{
    Task<AuthenticationSession?> FindAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<AuthenticationSession?> FindOpenAsync(Guid userId, CancellationToken cancellationToken);
    Task<RefreshTokenRecord?> FindRefreshAsync(Guid tokenId, CancellationToken cancellationToken);
    void Add(AuthenticationSession session);
    void Add(RefreshTokenRecord refresh);
}

// Serializes transitions for this local user, reloading state inside the transaction.
// No network call belongs in the callback. Commit is the success boundary.
public interface IUnitOfWork
{
    Task<T> ExecuteAsync<T>(Guid userId, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);
}

public sealed record TokenIdentity(Guid UserId, Guid SessionId, Guid TokenId, DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt, string? Role);
public sealed record IssuedToken(string Value, Guid Id, string Hash, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt)
{
    public override string ToString() => nameof(IssuedToken);
}
public sealed record TokenPair(IssuedToken Access, IssuedToken Refresh);
public interface ITokenService
{
    TokenPair? Issue(AuthenticationSession session);
    TokenIdentity? Validate(string token, string use);
    string Hash(string token);
}

public interface ISessionStatusQuery
{
    Task<bool> IsActiveAsync(TokenIdentity identity, CancellationToken cancellationToken);
}

public sealed record RatePolicy(string Name, int Limit, int WindowSeconds);
public sealed record RateDecision(bool Allowed, DateTimeOffset DecisionTime, DateTimeOffset? NextAllowedAt)
{
    public int RetryAfterSeconds => Math.Max(1, (int)Math.Ceiling(((NextAllowedAt ?? DecisionTime.AddSeconds(1)) - DecisionTime).TotalSeconds));
}
public interface IAuthenticationRateLimitStore
{
    Task<RateDecision> AdmitAsync(RatePolicy policy, string subject, CancellationToken cancellationToken);
}

public sealed class PersistenceFailureException(FailureCode code, Exception? inner = null)
    : Exception("Authentication persistence operation failed.", inner)
{
    public FailureCode Code { get; } = code;
}

public sealed record AuthenticatedUser(string Id, string DisplayName, string Role, string? DefaultWorkshopId);
public sealed record AuthenticationResult(TokenPair Tokens, AuthenticatedUser? User);

public sealed record RetentionPolicy(int SessionRetentionDays, int RefreshRetentionHoursAfterSessionEnd,
    int AuditRetentionDays, int CleanupIntervalHours, int BatchSize);
public sealed record CleanupResult(bool Acquired, int RefreshRecords, int Sessions, int AuditEntries, bool More);
public interface IAuthenticationRetentionStore
{
    Task<CleanupResult> CleanBatchAsync(string owner, RetentionPolicy policy, CancellationToken cancellationToken);
}
