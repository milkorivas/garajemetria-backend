using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GarajeMetria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GarajeMetria.IntegrationTests;

// Isolated certificates/configuration shared by the preparation and real OAuth HTTP tests.
public sealed class LprTestEnvironment : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "GarajeMetria_LprTests_" + Guid.NewGuid().ToString("N"));
    public string SigningCertificatePath => Path.Combine(directory, "lpr-signing.pfx");
    public string EncryptionCertificatePath => Path.Combine(directory, "lpr-encryption.pfx");
    public string CertificatePassword { get; } = Guid.NewGuid().ToString("N");
    public string RateLimitNamespace { get; } = "lpr-test-" + Guid.NewGuid().ToString("N");

    public LprTestEnvironment()
    {
        Directory.CreateDirectory(directory);
        try
        {
            CreateCertificate(SigningCertificatePath, X509KeyUsageFlags.DigitalSignature);
            CreateCertificate(EncryptionCertificatePath, X509KeyUsageFlags.KeyEncipherment);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public object DeviceOAuth => new
    {
        AccessTokenMinutes = 15,
        Issuer = "https://synthetic-lpr.test/",
        Audience = "garajemetria-lpr",
        SigningCertificate = SigningCertificatePath,
        SigningCertificatePassword = CertificatePassword,
        EncryptionCertificate = EncryptionCertificatePath,
        EncryptionCertificatePassword = CertificatePassword,
        TokensPerClient = 10,
        TokensPerIp = 120
    };

    public object Lpr => new
    {
        RateLimitNamespace,
        MaxEventAgeMinutes = 30,
        MaxFutureMinutes = 30,
        RequestTimeoutSeconds = 15,
        LockTimeoutSeconds = 5,
        RateLimitWindowSeconds = 60,
        EventsPerDevice = 120,
        EventsPerIp = 1200,
        AdminWritesPerUser = 30
    };

    private void CreateCertificate(string path, X509KeyUsageFlags usage)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=GarajeMetria-Lpr-Synthetic-Test", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, CertificatePassword));
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(directory);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("GarajeMetria_LprTests_", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove an unexpected test directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}

public sealed class LprPreparationTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task TwoHostsShareIsolatedSqlAndLprKeysWithoutCallingCorporateDirectory()
    {
        using var environment = new LprTestEnvironment();
        using var first = new AuthenticationFactory(database, lprEnvironment: environment);
        using var firstClient = first.Client();
        using var second = new AuthenticationFactory(database, shared: first, lprEnvironment: environment);
        using var secondClient = second.Client();
        Assert.Equal(HttpStatusCode.OK, (await firstClient.GetAsync("/api/auth/antiforgery")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await secondClient.GetAsync("/api/auth/antiforgery")).StatusCode);
        Assert.Equal(0, first.DirectoryAdapter.Calls);
        Assert.Equal(0, second.DirectoryAdapter.Calls);
        using var firstScope = first.Services.CreateScope();
        using var secondScope = second.Services.CreateScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<AuthenticationDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<AuthenticationDbContext>();
        Assert.NotSame(firstDb, secondDb);
        Assert.Equal(database.DatabaseName, firstDb.Database.GetDbConnection().Database);
        Assert.Equal(database.DatabaseName, secondDb.Database.GetDbConnection().Database);
        Assert.Empty(await firstDb.Database.GetPendingMigrationsAsync());
        Assert.Empty(await secondDb.Database.GetPendingMigrationsAsync());
        foreach (var provider in new[] { first.Services, second.Services })
        {
            var config = provider.GetRequiredService<IConfiguration>();
            Assert.Equal("https://synthetic-lpr.test/", config["DeviceOAuth:Issuer"]);
            Assert.Equal(environment.SigningCertificatePath, config["DeviceOAuth:SigningCertificate"]);
            Assert.Equal(environment.EncryptionCertificatePath, config["DeviceOAuth:EncryptionCertificate"]);
            Assert.Equal("30", config["Lpr:MaxEventAgeMinutes"]);
        }
        using var signing = Load(environment.SigningCertificatePath, environment.CertificatePassword);
        using var encryption = Load(environment.EncryptionCertificatePath, environment.CertificatePassword);
        Assert.True(signing.HasPrivateKey);
        Assert.True(encryption.HasPrivateKey);
        Assert.NotEqual(signing.Thumbprint, encryption.Thumbprint);
        Assert.Equal(X509KeyUsageFlags.DigitalSignature, signing.Extensions.OfType<X509KeyUsageExtension>().Single().KeyUsages);
        Assert.Equal(X509KeyUsageFlags.KeyEncipherment, encryption.Extensions.OfType<X509KeyUsageExtension>().Single().KeyUsages);
    }

    private static X509Certificate2 Load(string path, string password) =>
        X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
}
