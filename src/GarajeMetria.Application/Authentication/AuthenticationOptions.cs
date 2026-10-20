using System.Text;

namespace GarajeMetria.Application.Authentication;

public sealed class AuthenticationOptions
{
    public int AccessTokenLifetimeSeconds { get; set; }
    public int RefreshTokenLifetimeSeconds { get; set; }
    public int MaxSessionLifetimeSeconds { get; set; }
    public int MaxUsernameLength { get; set; }
    public int MaxPasswordLength { get; set; }
    public int MaxLoginBodyBytes { get; set; }
    public int MaxJsonDepth { get; set; }
    public string DefaultDomain { get; set; } = "";
    public string[] AllowedDomains { get; set; } = [];
    public string RateLimitNamespace { get; set; } = "";
    public int LoginIpLimit { get; set; }
    public int LoginUserLimit { get; set; }
    public int RefreshIpLimit { get; set; }
    public int RefreshUserLimit { get; set; }
    public int RateLimitWindowSeconds { get; set; }
    public int SessionRetentionDays { get; set; }
    public int RefreshRetentionHoursAfterSessionEnd { get; set; }
    public int AuditRetentionDays { get; set; }
    public int CleanupIntervalHours { get; set; }
    public int CleanupBatchSize { get; set; }

    public RatePolicy Policy(string name) => new(name, name switch
    {
        "login-ip" => LoginIpLimit,
        "login-user" => LoginUserLimit,
        "refresh-ip" => RefreshIpLimit,
        "refresh-user" => RefreshUserLimit,
        _ => throw new ArgumentException("Unknown policy.", nameof(name))
    }, RateLimitWindowSeconds);

    public RetentionPolicy Retention => new(SessionRetentionDays, RefreshRetentionHoursAfterSessionEnd,
        AuditRetentionDays, CleanupIntervalHours, CleanupBatchSize);
}

public static class InputRules
{
    public static int ScalarCount(string value)
    {
        var count = 0;
        var span = value.AsSpan();
        while (!span.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(span, out _, out var consumed) != System.Buffers.OperationStatus.Done) return -1;
            span = span[consumed..];
            count++;
        }
        return count;
    }

    public static bool ValidUsername(string value, int maximum)
    {
        var length = ScalarCount(value);
        return length > 0 && length <= maximum && !value.Any(char.IsControl) &&
            !value.Contains('/') && !value.Contains('@') && value.Split('\\') is { Length: <= 2 } parts &&
            parts.All(p => p.Length > 0 && !char.IsWhiteSpace(p[0]) && !char.IsWhiteSpace(p[^1]));
    }

    public static (string Domain, string Username) Resolve(string value, string defaultDomain)
    {
        var parts = value.Split('\\');
        return (parts.Length == 2 ? parts[0] : defaultDomain, parts[^1]);
    }

    public static string Normalize(string value) => value.ToUpperInvariant();
}
