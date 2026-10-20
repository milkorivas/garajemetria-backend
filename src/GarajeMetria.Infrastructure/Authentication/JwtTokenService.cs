using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Domain.Authentication;
using Microsoft.IdentityModel.Tokens;

namespace GarajeMetria.Infrastructure.Authentication;

public sealed class JwtTokenService : ITokenService, IDisposable
{
    private readonly JwtOptions jwt;
    private readonly AuthenticationOptions options;
    private readonly TimeProvider clock;
    private readonly X509Certificate2 signingCertificate;
    private readonly List<X509Certificate2> certificates = [];
    private readonly Dictionary<string, SecurityKey> keys = new(StringComparer.Ordinal);
    private readonly JwtSecurityTokenHandler handler = new() { MapInboundClaims = false, MaximumTokenSizeInBytes = 16384 };

    public JwtTokenService(JwtOptions jwt, AuthenticationOptions options, TimeProvider clock)
    {
        this.jwt = jwt;
        this.options = options;
        this.clock = clock;
        signingCertificate = X509CertificateLoader.LoadPkcs12FromFile(jwt.SigningCertificatePath, jwt.SigningCertificatePassword,
            X509KeyStorageFlags.EphemeralKeySet);
        certificates.Add(signingCertificate);
        using var signingRsa = signingCertificate.GetRSAPrivateKey();
        if (!signingCertificate.HasPrivateKey || signingRsa is not { KeySize: >= 2048 })
            throw new InvalidOperationException("A valid RSA signing certificate is required.");
        keys.Add(jwt.SigningKeyId, new X509SecurityKey(signingCertificate) { KeyId = jwt.SigningKeyId });
        foreach (var verification in jwt.VerificationKeys)
        {
            var cert = X509CertificateLoader.LoadCertificateFromFile(verification.CertificatePath);
            certificates.Add(cert);
            using var verificationRsa = cert.GetRSAPublicKey();
            if (verificationRsa is not { KeySize: >= 2048 } || !keys.TryAdd(verification.KeyId, new X509SecurityKey(cert) { KeyId = verification.KeyId }))
                throw new InvalidOperationException("Invalid verification key configuration.");
        }
    }

    public TokenPair? Issue(AuthenticationSession session)
    {
        var now = clock.GetUtcNow();
        var issuedAt = TokenTimes.FloorSecond(now);
        var accessEnd = TokenTimes.Expires(issuedAt, TimeSpan.FromSeconds(options.AccessTokenLifetimeSeconds), session.AbsoluteExpiresAt);
        var refreshEnd = TokenTimes.Expires(issuedAt, TimeSpan.FromSeconds(options.RefreshTokenLifetimeSeconds), session.AbsoluteExpiresAt);
        if (!session.IsActive(now) || accessEnd <= now || refreshEnd <= now) return null;
        return new(IssueOne(session, "access", issuedAt, accessEnd), IssueOne(session, "refresh", issuedAt, refreshEnd));
    }

    private IssuedToken IssueOne(AuthenticationSession session, string use, DateTimeOffset issuedAt, DateTimeOffset expires)
    {
        var id = Guid.NewGuid();
        var payload = new JwtPayload
        {
            ["iss"] = jwt.Issuer,
            ["aud"] = use == "access" ? jwt.AccessAudience : jwt.RefreshAudience,
            ["sub"] = session.UserId.ToString(),
            ["sid"] = session.Id.ToString(),
            ["jti"] = id.ToString(),
            ["iat"] = issuedAt.ToUnixTimeSeconds(),
            ["nbf"] = issuedAt.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["token_use"] = use
        };
        if (use == "access") payload["role"] = session.Role;
        var token = handler.WriteToken(new JwtSecurityToken(new JwtHeader(new SigningCredentials(keys[jwt.SigningKeyId], SecurityAlgorithms.RsaSha256)), payload));
        return new(token, id, Hash(token), issuedAt, expires);
    }

    public TokenIdentity? Validate(string token, string use)
    {
        if (use is not ("access" or "refresh") || string.IsNullOrEmpty(token) || token.Length > 16384) return null;
        try
        {
            var segments = token.Split('.');
            if (segments.Length != 3) return null;
            using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(segments[0]));
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(segments[1]));
            if (!UniqueObject(header.RootElement) || !UniqueObject(payload.RootElement)) return null;
            var h = header.RootElement;
            var p = payload.RootElement;
            if (Text(h, "alg") != SecurityAlgorithms.RsaSha256 || Text(h, "kid") is not { } kid || !keys.ContainsKey(kid) ||
                h.TryGetProperty("crit", out _) || Text(p, "token_use") != use || Text(p, "iss") != jwt.Issuer ||
                Text(p, "aud") != (use == "access" ? jwt.AccessAudience : jwt.RefreshAudience)) return null;
            if (!Guid.TryParse(Text(p, "sub"), out var user) || user == Guid.Empty ||
                !Guid.TryParse(Text(p, "sid"), out var session) || session == Guid.Empty ||
                !Guid.TryParse(Text(p, "jti"), out var id) || id == Guid.Empty) return null;
            if (!Integer(p, "iat", out var iat) || !Integer(p, "nbf", out var nbf) || !Integer(p, "exp", out var exp) || iat != nbf || exp <= nbf) return null;
            var issued = DateTimeOffset.FromUnixTimeSeconds(iat);
            var expires = DateTimeOffset.FromUnixTimeSeconds(exp);
            var now = clock.GetUtcNow();
            if (now < issued || now >= expires) return null;
            var role = Text(p, "role");
            if (use == "access" ? !UserRoles.IsValid(role) : p.TryGetProperty("role", out _)) return null;
            handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidIssuer = jwt.Issuer,
                ValidAudience = use == "access" ? jwt.AccessAudience : jwt.RefreshAudience,
                IssuerSigningKey = keys[kid],
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                ValidateIssuer = true,
                ValidateAudience = true,
                RequireAudience = true,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds!.Value),
                RoleClaimType = "role",
                NameClaimType = "sub",
                LifetimeValidator = (_, _, _, _) => clock.GetUtcNow() >= issued && clock.GetUtcNow() < expires
            }, out _);
            return new(user, session, id, issued, expires, role);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException or JsonException or FormatException or OverflowException or InvalidOperationException)
        {
            return null;
        }
    }

    public string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static bool UniqueObject(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();
    private static string? Text(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Integer(JsonElement p, string name, out long value)
    {
        value = 0;
        return p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
            !v.GetRawText().Any(c => c is '.' or 'e' or 'E') && v.TryGetInt64(out value);
    }
    public void Dispose() { foreach (var certificate in certificates) certificate.Dispose(); }
}
