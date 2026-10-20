namespace GarajeMetria.Domain.Authentication;

public static class UserRoles
{
    public const string Administrator = "administrator";
    public const string Advisor = "advisor";

    public static string? Resolve(bool administrator, bool advisor) =>
        administrator ? Administrator : advisor ? Advisor : null;

    public static bool IsValid(string? role) => role is Administrator or Advisor;
}

public enum WorkshopAccessDecision { Allowed, WorkshopRequired, CatalogUnavailable, WorkshopInactive }

public static class UserAccessPolicy
{
    public static WorkshopAccessDecision Evaluate(string role, bool hasWorkshop, bool catalogAvailable, bool workshopActive)
    {
        if (!UserRoles.IsValid(role)) throw new ArgumentException("Invalid role.", nameof(role));
        return role == UserRoles.Administrator ? WorkshopAccessDecision.Allowed :
            !hasWorkshop ? WorkshopAccessDecision.WorkshopRequired :
            !catalogAvailable ? WorkshopAccessDecision.CatalogUnavailable :
            !workshopActive ? WorkshopAccessDecision.WorkshopInactive : WorkshopAccessDecision.Allowed;
    }
}

public sealed class AuthenticationSession
{
    private AuthenticationSession() { }

    public AuthenticationSession(Guid id, Guid userId, string role, DateTimeOffset startedAt,
        DateTimeOffset absoluteExpiresAt)
    {
        if (id == Guid.Empty || userId == Guid.Empty || !UserRoles.IsValid(role) ||
            startedAt.Offset != TimeSpan.Zero || absoluteExpiresAt.Offset != TimeSpan.Zero || absoluteExpiresAt <= startedAt)
        {
            throw new ArgumentException("Invalid session.");
        }

        Id = id;
        UserId = userId;
        Role = role;
        StartedAt = startedAt;
        AbsoluteExpiresAt = absoluteExpiresAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Role { get; private set; } = "";
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset AbsoluteExpiresAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public string? CloseReason { get; private set; }
    public bool IsActive(DateTimeOffset now) => ClosedAt is null && now < AbsoluteExpiresAt;
    public bool Allows(Guid userId, string? role, DateTimeOffset now) => UserId == userId && Role == role && IsActive(now);
    public DateTimeOffset EndedAt => ClosedAt is { } closed && closed < AbsoluteExpiresAt ? closed : AbsoluteExpiresAt;

    public void Close(DateTimeOffset now, string reason)
    {
        if (ClosedAt is not null) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ClosedAt = now.ToUniversalTime();
        CloseReason = reason;
    }
}

public enum RefreshUse { Accepted, Invalid, Expired, Reused }

public sealed class RefreshTokenRecord
{
    private RefreshTokenRecord() { }

    public RefreshTokenRecord(Guid id, Guid sessionId, string tokenHash, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        if (id == Guid.Empty || sessionId == Guid.Empty || tokenHash.Length != 64 || expiresAt <= issuedAt ||
            issuedAt.Ticks % TimeSpan.TicksPerSecond != 0 || expiresAt.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException("Invalid refresh record.");
        }
        Id = id;
        SessionId = sessionId;
        TokenHash = tokenHash;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public string TokenHash { get; private set; } = "";
    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public RefreshUse Inspect(AuthenticationSession session, DateTimeOffset now)
    {
        if (now >= ExpiresAt) return RefreshUse.Expired;
        if (session.Id != SessionId || !session.IsActive(now) || now < IssuedAt || RevokedAt is not null) return RefreshUse.Invalid;
        return ConsumedAt is null ? RefreshUse.Accepted : RefreshUse.Reused;
    }

    public RefreshUse Consume(AuthenticationSession session, DateTimeOffset now)
    {
        var result = Inspect(session, now);
        if (result == RefreshUse.Accepted) ConsumedAt = now;
        return result;
    }
}

public static class TokenTimes
{
    public static DateTimeOffset FloorSecond(DateTimeOffset value) => DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());
    public static DateTimeOffset Expires(DateTimeOffset issuedAt, TimeSpan lifetime, DateTimeOffset sessionEnd) =>
        FloorSecond(issuedAt + lifetime < sessionEnd ? issuedAt + lifetime : sessionEnd);
}
