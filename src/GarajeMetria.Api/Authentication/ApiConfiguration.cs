using GarajeMetria.Application.Authentication;
using Microsoft.AspNetCore.DataProtection;

namespace GarajeMetria.Api.Authentication;

public sealed class HttpOptions
{
    public string[] AllowedOrigins { get; set; } = [];
    public string[] TrustedProxies { get; set; } = [];
    public string DataProtectionKeyPath { get; set; } = "";
    public string DataProtectionCertificatePath { get; set; } = "";
    public bool EnableApiDocumentation { get; set; }
}

public static class ApiConfiguration
{
    public static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathFullyQualified(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static AuthenticationOptions ReadAuthenticationOptions(IConfiguration configuration)
    {
        var o = configuration.GetRequiredSection("Authentication").Get<AuthenticationOptions>() ?? new();
        if (o.AccessTokenLifetimeSeconds <= 0 || o.RefreshTokenLifetimeSeconds <= 0 || o.MaxSessionLifetimeSeconds <= 0 ||
            o.AccessTokenLifetimeSeconds > 31536000 || o.RefreshTokenLifetimeSeconds > 31536000 || o.MaxSessionLifetimeSeconds > 31536000 ||
            o.MaxUsernameLength <= 0 || o.MaxPasswordLength < 64 || o.MaxLoginBodyBytes <= 0 || o.MaxLoginBodyBytes > 1048576 ||
            o.MaxJsonDepth != 2 || o.MaxLoginBodyBytes < 12L * (o.MaxUsernameLength + (long)o.MaxPasswordLength) + 64 ||
            !InputRules.ValidUsername(o.DefaultDomain, o.MaxUsernameLength) || o.DefaultDomain.Contains('\\') ||
            o.AllowedDomains.Length != 1 || !o.AllowedDomains.Contains(o.DefaultDomain, StringComparer.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(o.RateLimitNamespace) || o.RateLimitNamespace.Length > 128 ||
            o.LoginIpLimit <= 0 || o.LoginUserLimit <= 0 || o.RefreshIpLimit <= 0 || o.RefreshUserLimit <= 0 || o.RateLimitWindowSeconds is <= 0 or > 86400 ||
            o.SessionRetentionDays is <= 0 or > 36500 || o.RefreshRetentionHoursAfterSessionEnd is <= 0 or > 876000 ||
            o.SessionRetentionDays * 24L < o.RefreshRetentionHoursAfterSessionEnd || o.AuditRetentionDays is <= 0 or > 36500 ||
            o.CleanupIntervalHours is <= 0 or > 8760 || o.CleanupBatchSize is <= 0 or > 10000)
            throw new InvalidOperationException("Invalid or missing Authentication configuration.");
        return o;
    }

    public static HttpOptions ReadHttpOptions(IConfiguration configuration)
    {
        var o = configuration.GetRequiredSection("Http").Get<HttpOptions>() ?? new();
        if (o.AllowedOrigins.Length == 0)
            throw new InvalidOperationException("Http:AllowedOrigins must contain at least one explicit HTTPS origin.");
        for (var i = 0; i < o.AllowedOrigins.Length; i++)
        {
            if (!OriginRules.IsConfiguredOrigin(o.AllowedOrigins[i]))
                throw new InvalidOperationException($"Http:AllowedOrigins:{i} must be an explicit HTTPS origin without a trailing slash, path, query or fragment.");
        }
        if (string.IsNullOrWhiteSpace(o.DataProtectionKeyPath) || !Path.IsPathFullyQualified(o.DataProtectionKeyPath))
            throw new InvalidOperationException("Http:DataProtectionKeyPath must be an absolute directory path.");
        if (string.IsNullOrWhiteSpace(o.DataProtectionCertificatePath))
            throw new InvalidOperationException("Http:DataProtectionCertificatePath is required.");
        for (var i = 0; i < o.TrustedProxies.Length; i++)
        {
            if (!System.Net.IPAddress.TryParse(o.TrustedProxies[i], out _))
                throw new InvalidOperationException($"Http:TrustedProxies:{i} must be a valid IP address.");
        }
        return o;
    }
}

public static class OriginRules
{
    public static bool IsConfiguredOrigin(string value) => TryOrigin(value, true, out var uri) && uri!.Scheme == "https";

    public static bool IsAllowed(HttpRequest request, HttpOptions options)
    {
        if (request.Headers["Sec-Fetch-Site"].SelectMany(x => (x ?? "").Split(','))
            .Any(x => string.Equals(x.Trim(), "cross-site", StringComparison.OrdinalIgnoreCase))) return false;
        if (request.Headers.TryGetValue("Origin", out var origin))
            return origin.Count == 1 && TryOrigin(origin[0], true, out var parsed) && Matches(parsed!, options);
        if (request.Headers.TryGetValue("Referer", out var referer))
            return referer.Count == 1 && TryOrigin(referer[0], false, out var parsed) && Matches(parsed!, options);
        return true;
    }

    private static bool Matches(Uri origin, HttpOptions options) => options.AllowedOrigins.Any(value =>
    {
        var allowed = new Uri(value);
        return allowed.Scheme == origin.Scheme && allowed.IdnHost == origin.IdnHost && allowed.Port == origin.Port;
    });

    private static bool TryOrigin(string? value, bool originOnly, out Uri? uri)
    {
        uri = null;
        return !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsWhiteSpace) && !value.Contains(',') &&
            Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https" &&
            uri.UserInfo.Length == 0 && (!originOnly || uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
                !value.EndsWith('/'));
    }
}
