using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Domain.Authentication;
using GarajeMetria.Infrastructure.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace GarajeMetria.IntegrationTests;

public sealed class JwtContractTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "GarajeMetria_JwtTests_" + Guid.NewGuid().ToString("N"));
    private readonly RSA key = RSA.Create(2048);
    private readonly TestClock clock = new() { Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(321) };
    private readonly JwtTokenService service;
    private readonly AuthenticationSession session;
    private readonly RecordingLogProvider logs = new();
    private readonly ILoggerFactory logFactory;

    public JwtContractTests()
    {
        logFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        Directory.CreateDirectory(folder);
        var request = new CertificateRequest("CN=Synthetic-JWT-Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        var path = Path.Combine(folder, "signing.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, "synthetic"));
        service = new(new JwtOptions
        {
            Issuer = "synthetic",
            AccessAudience = "access-audience",
            RefreshAudience = "refresh-audience",
            ClockSkewSeconds = 0,
            SigningCertificatePath = path,
            SigningCertificatePassword = "synthetic",
            SigningKeyId = "test-key"
        },
            new AuthenticationOptions { AccessTokenLifetimeSeconds = 600, RefreshTokenLifetimeSeconds = 1800 }, clock,
            logFactory.CreateLogger<JwtTokenService>());
        session = new(Guid.NewGuid(), Guid.NewGuid(), UserRoles.Administrator, clock.Now, clock.Now.AddHours(8));
    }

    [Fact]
    public void RejectedTokenExceptionsAreLoggedWithoutTheTokenOrExceptionMessage()
    {
        Assert.Null(service.Validate("!.private-token.!", "access"));
        var entry = Assert.Single(logs.Entries);
        Assert.Equal("Jwt.ValidationRejected", entry.Properties["Operation"]);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.DoesNotContain("private-token", entry.Message);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void EmissionAndValidationUseExactClaimsSecondsAndSeparateAudiences()
    {
        var pair = service.Issue(session)!;
        var access = service.Validate(pair.Access.Value, "access");
        var refresh = service.Validate(pair.Refresh.Value, "refresh");
        Assert.NotNull(access);
        Assert.NotNull(refresh);
        Assert.Equal("administrator", access.Role);
        Assert.Null(refresh.Role);
        Assert.Equal(0, access.ExpiresAt.Ticks % TimeSpan.TicksPerSecond);
        Assert.Equal(pair.Access.ExpiresAt, access.ExpiresAt);
        Assert.Null(service.Validate(pair.Access.Value, "refresh"));
        Assert.Null(service.Validate(pair.Refresh.Value, "access"));
        Assert.NotEqual(pair.Access.Id, pair.Refresh.Id);
        Assert.Equal(64, pair.Refresh.Hash.Length);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void ExpiryIsExclusiveAtFullClockPrecision(int ticks, bool valid)
    {
        var pair = service.Issue(session)!;
        clock.Now = pair.Access.ExpiresAt.AddTicks(ticks);
        Assert.Equal(valid, service.Validate(pair.Access.Value, "access") is not null);
    }

    [Fact]
    public void NotBeforeRejectsEarlierInstants()
    {
        var pair = service.Issue(session)!;
        clock.Now = pair.Access.IssuedAt.AddTicks(-1);
        Assert.Null(service.Validate(pair.Access.Value, "access"));
    }

    [Fact]
    public void SubsecondRemainderAtAbsoluteEndCannotProduceAnAlreadyExpiredToken()
    {
        var shortSession = new AuthenticationSession(Guid.NewGuid(), Guid.NewGuid(), "administrator", clock.Now, clock.Now.AddSeconds(1));
        clock.Now = shortSession.AbsoluteExpiresAt.AddTicks(-1);
        Assert.Null(service.Issue(shortSession));
    }

    [Theory]
    [InlineData("token_use", "\"Access\"")]
    [InlineData("token_use", "[\"access\"]")]
    [InlineData("token_use", "null")]
    [InlineData("role", "\"Administrador\"")]
    [InlineData("role", "[\"administrator\",\"advisor\"]")]
    [InlineData("exp", "\"1767226200\"")]
    [InlineData("exp", "1767226200.1")]
    [InlineData("exp", "1.7672262e9")]
    [InlineData("aud", "[\"access-audience\"]")]
    [InlineData("iss", "\"other\"")]
    [InlineData("sub", "\"not-an-id\"")]
    [InlineData("sid", "null")]
    public void SignedButNonconformingClaimsAreRejected(string claim, string jsonValue)
    {
        var payload = Payload();
        payload[claim] = JsonNode.Parse(jsonValue);
        Assert.Null(service.Validate(Sign(payload.ToJsonString()), "access"));
    }

    [Theory]
    [InlineData("token_use")]
    [InlineData("role")]
    [InlineData("iat")]
    [InlineData("nbf")]
    [InlineData("exp")]
    [InlineData("sub")]
    [InlineData("sid")]
    [InlineData("jti")]
    public void RequiredClaimsCannotBeMissing(string claim)
    {
        var payload = Payload();
        payload.Remove(claim);
        Assert.Null(service.Validate(Sign(payload.ToJsonString()), "access"));
    }

    [Fact]
    public void DuplicateClaimsCannotBeHiddenByJsonDeserialization()
    {
        var payload = Payload().ToJsonString();
        Assert.Null(service.Validate(Sign(payload[..^1] + ",\"token_use\":\"access\"}"), "access"));
    }

    [Fact]
    public void IncorrectSignatureAndUnknownKeyAreRejected()
    {
        var pair = service.Issue(session)!;
        var parts = pair.Access.Value.Split('.');
        var signature = Base64UrlEncoder.DecodeBytes(parts[2]);
        signature[0] ^= 1;
        Assert.Null(service.Validate(parts[0] + "." + parts[1] + "." + Base64UrlEncoder.Encode(signature), "access"));
        Assert.Null(service.Validate(Sign(Payload().ToJsonString(), "unknown-key"), "access"));
    }

    private JsonObject Payload() => JsonNode.Parse(Base64UrlEncoder.Decode(service.Issue(session)!.Access.Value.Split('.')[1]))!.AsObject();
    private string Sign(string payload, string kid = "test-key")
    {
        var unsigned = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new { alg = "RS256", kid, typ = "JWT" })) + "." + Base64UrlEncoder.Encode(payload);
        return unsigned + "." + Base64UrlEncoder.Encode(key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    public void Dispose()
    {
        service.Dispose();
        logFactory.Dispose();
        key.Dispose();
        var resolved = Path.GetFullPath(folder);
        if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(resolved).StartsWith("GarajeMetria_JwtTests_", StringComparison.Ordinal)) Directory.Delete(resolved, true);
    }
}
