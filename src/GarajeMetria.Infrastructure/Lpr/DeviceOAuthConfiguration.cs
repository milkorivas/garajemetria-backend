using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GarajeMetria.Application.Lpr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GarajeMetria.Infrastructure.Lpr;

public sealed class DeviceOAuthOptions
{
    public int AccessTokenMinutes { get; set; } = 15;
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public string SigningCertificate { get; set; } = "";
    public string SigningCertificatePassword { get; set; } = "";
    public string EncryptionCertificate { get; set; } = "";
    public string EncryptionCertificatePassword { get; set; } = "";
    public int TokensPerClient { get; set; } = 10;
    public int TokensPerIp { get; set; } = 120;
    public int DeviceTokenRetentionHours { get; set; } = 24;
    public override string ToString() => nameof(DeviceOAuthOptions);
}

internal static class LprConfiguration
{
    internal static (LprOptions Lpr, DeviceOAuthOptions OAuth) Read(IConfiguration configuration)
    {
        var lpr = configuration.GetRequiredSection("Lpr").Get<LprOptions>() ?? new();
        var oauth = configuration.GetRequiredSection("DeviceOAuth").Get<DeviceOAuthOptions>() ?? new();
        lpr.TokensPerClient = oauth.TokensPerClient;
        lpr.TokensPerIp = oauth.TokensPerIp;
        if (lpr.MaxEventAgeMinutes is < 0 or > 1440 || lpr.MaxFutureMinutes is < 0 or > 1440 ||
            lpr.RateLimitWindowSeconds is <= 0 or > 86400 || string.IsNullOrWhiteSpace(lpr.RateLimitNamespace) ||
            lpr.RateLimitNamespace.Length > 128 || lpr.EventsPerDevice <= 0 || lpr.EventsPerIp <= 0 ||
            lpr.AdminWritesPerUser <= 0 || oauth.TokensPerClient <= 0 || oauth.TokensPerIp <= 0 ||
            oauth.AccessTokenMinutes is <= 0 or > 60 || oauth.DeviceTokenRetentionHours is <= 0 or > 876000 ||
            oauth.Audience != "garajemetria-lpr" || oauth.Audience == configuration["Jwt:AccessAudience"] ||
            oauth.Audience == configuration["Jwt:RefreshAudience"] || oauth.Issuer == configuration["Jwt:Issuer"] ||
            !Uri.TryCreate(oauth.Issuer, UriKind.Absolute, out var issuer) ||
            issuer.Scheme != "https" || !string.IsNullOrEmpty(issuer.UserInfo) || !string.IsNullOrEmpty(issuer.Query) || !string.IsNullOrEmpty(issuer.Fragment) ||
            string.IsNullOrWhiteSpace(oauth.SigningCertificate) || !Path.IsPathFullyQualified(oauth.SigningCertificate) ||
            string.IsNullOrWhiteSpace(oauth.EncryptionCertificate) || !Path.IsPathFullyQualified(oauth.EncryptionCertificate))
            throw new InvalidOperationException("Invalid Lpr or DeviceOAuth configuration.");
        return (lpr, oauth);
    }
}

internal sealed class DeviceOAuthCertificates : IDisposable
{
    public X509Certificate2 Signing { get; }
    public X509Certificate2 Encryption { get; }
    public DeviceOAuthCertificates(DeviceOAuthOptions options, TimeProvider clock)
    {
        Signing = Load(options.SigningCertificate, options.SigningCertificatePassword, X509KeyUsageFlags.DigitalSignature, clock);
        try { Encryption = Load(options.EncryptionCertificate, options.EncryptionCertificatePassword, X509KeyUsageFlags.KeyEncipherment, clock); }
        catch { Signing.Dispose(); throw; }
    }
    private static X509Certificate2 Load(string path, string password, X509KeyUsageFlags usage, TimeProvider clock)
    {
        X509Certificate2? certificate = null;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
            using var rsa = certificate.GetRSAPrivateKey();
            var now = clock.GetUtcNow();
            if (!certificate.HasPrivateKey || rsa is null || rsa.KeySize < 2048 ||
                now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime() ||
                certificate.Extensions.OfType<X509KeyUsageExtension>().Any(x => (x.KeyUsages & usage) != usage))
                throw new InvalidOperationException();
            return certificate;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            certificate?.Dispose();
            throw new InvalidOperationException("Invalid DeviceOAuth certificate configuration.");
        }
    }
    public void Dispose() { Signing.Dispose(); Encryption.Dispose(); }
}

internal sealed class DeviceOAuthStartup(DeviceOAuthCertificates certificates) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) { _ = certificates; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
