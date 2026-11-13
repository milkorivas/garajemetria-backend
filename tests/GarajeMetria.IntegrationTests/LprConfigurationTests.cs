using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using GarajeMetria.Infrastructure.Lpr;
using GarajeMetria.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace GarajeMetria.IntegrationTests;

public sealed class LprConfigurationTests
{
    [Theory]
    [InlineData("Lpr:MaxEventAgeMinutes", "-1")]
    [InlineData("Lpr:MaxFutureMinutes", "1441")]
    [InlineData("Lpr:RateLimitWindowSeconds", "0")]
    [InlineData("Lpr:RateLimitNamespace", "")]
    [InlineData("Lpr:EventsPerDevice", "0")]
    [InlineData("Lpr:EventsPerIp", "-1")]
    [InlineData("Lpr:AdminWritesPerUser", "0")]
    [InlineData("DeviceOAuth:AccessTokenMinutes", "61")]
    [InlineData("DeviceOAuth:TokensPerClient", "0")]
    [InlineData("DeviceOAuth:TokensPerIp", "0")]
    [InlineData("DeviceOAuth:DeviceTokenRetentionHours", "0")]
    [InlineData("DeviceOAuth:Issuer", "http://synthetic.test")]
    [InlineData("DeviceOAuth:Audience", "user-audience")]
    [InlineData("DeviceOAuth:SigningCertificate", "relative.pfx")]
    [InlineData("DeviceOAuth:EncryptionCertificate", "")]
    public void InvalidConfigurationIsRejectedWithoutEchoingPrivateValues(string key, string value)
    {
        using var environment = new LprTestEnvironment();
        var config = Configuration(environment);
        config[key] = value;
        var exception = Assert.Throws<InvalidOperationException>(() => LprConfiguration.Read(config));
        Assert.DoesNotContain(environment.CertificatePassword, exception.Message);
        Assert.DoesNotContain(environment.SigningCertificatePath, exception.Message);
    }

    [Fact]
    public void ValidConfigurationLoadsPrivateCertificatesAndRejectsExpiredOrWrongPasswordMaterial()
    {
        using var environment = new LprTestEnvironment();
        var (lpr, oauth) = LprConfiguration.Read(Configuration(environment));
        using var material = new DeviceOAuthCertificates(oauth, TimeProvider.System);
        Assert.True(material.Signing.HasPrivateKey);
        Assert.True(material.Encryption.HasPrivateKey);
        Assert.Equal(30, lpr.MaxEventAgeMinutes);
        Assert.Equal(15, oauth.AccessTokenMinutes);
        Assert.Throws<InvalidOperationException>(() => new DeviceOAuthCertificates(oauth, new ExpiredClock()));
        oauth.SigningCertificatePassword = "synthetic-wrong-password";
        var error = Assert.Throws<InvalidOperationException>(() => new DeviceOAuthCertificates(oauth, TimeProvider.System));
        Assert.DoesNotContain(oauth.SigningCertificatePassword, error.Message);
        Assert.DoesNotContain(environment.SigningCertificatePath, error.Message);
    }

    [Fact]
    public void MissingSectionsAndUserAudienceOrIssuerCollisionsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => LprConfiguration.Read(new ConfigurationBuilder().Build()));
        using var environment = new LprTestEnvironment();
        var config = Configuration(environment);
        config["Jwt:AccessAudience"] = "garajemetria-lpr";
        Assert.Throws<InvalidOperationException>(() => LprConfiguration.Read(config));
        config["Jwt:AccessAudience"] = "synthetic-user";
        config["Jwt:Issuer"] = config["DeviceOAuth:Issuer"];
        Assert.Throws<InvalidOperationException>(() => LprConfiguration.Read(config));
    }

    private static IConfiguration Configuration(LprTestEnvironment environment)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Lpr = environment.Lpr, DeviceOAuth = environment.DeviceOAuth })));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    [Fact]
    public void CertificateWithoutPrivateKeyIsRejected()
    {
        using var environment = new LprTestEnvironment();
        var (_, options) = LprConfiguration.Read(Configuration(environment));
        using var valid = new DeviceOAuthCertificates(options, TimeProvider.System);
        using var publicOnly = X509CertificateLoader.LoadCertificate(valid.Signing.RawData);
        var path = Path.Combine(Path.GetDirectoryName(environment.SigningCertificatePath)!, "public-only.pfx");
        File.WriteAllBytes(path, publicOnly.Export(X509ContentType.Pfx, environment.CertificatePassword));
        options.SigningCertificate = path;
        Assert.Throws<InvalidOperationException>(() => new DeviceOAuthCertificates(options, TimeProvider.System));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 5)]
    [InlineData(15, 0)]
    public void TimeoutsMustBePositiveAndLockTimeoutMustBeLessThanTotal(int request, int locks) =>
        Assert.Throws<InvalidOperationException>(() => new LprPersistenceOptions { RequestTimeoutSeconds = request, LockTimeoutSeconds = locks }.Validate());
    private sealed class ExpiredClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddDays(3);
    }
}
