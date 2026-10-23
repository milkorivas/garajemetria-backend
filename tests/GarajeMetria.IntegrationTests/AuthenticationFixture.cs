using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GarajeMetria.Api.Authentication;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GarajeMetria.IntegrationTests;

public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GARAJEMETRIA_TEST_SQL")))
            Skip = "Set GARAJEMETRIA_TEST_SQL to an isolated SQL Server test instance.";
    }
}

public sealed class SqlFixture : IAsyncLifetime
{
    public string DatabaseName { get; } = "GarajeMetria_AuthTests_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; private set; } = "";
    public async Task InitializeAsync()
    {
        var source = Environment.GetEnvironmentVariable("GARAJEMETRIA_TEST_SQL");
        if (!OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(source)) return;
        // Shared memory is local-only. CI may supply an isolated SQL service explicitly.
        var connection = new SqlConnectionStringBuilder(source ?? "Server=lpc:localhost;Integrated Security=true;Encrypt=false")
        { InitialCatalog = "master", ConnectTimeout = 10 };
        await using var master = new SqlConnection(connection.ConnectionString);
        await master.OpenAsync();
        await using var create = master.CreateCommand();
        create.CommandText = $"CREATE DATABASE [{DatabaseName}]";
        await create.ExecuteNonQueryAsync();
        connection.InitialCatalog = DatabaseName;
        ConnectionString = connection.ConnectionString;
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public AuthenticationDbContext CreateContext() => new(new DbContextOptionsBuilder<AuthenticationDbContext>()
        .UseSqlServer(ConnectionString).Options);

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        if (!DatabaseName.StartsWith("GarajeMetria_AuthTests_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(DatabaseName["GarajeMetria_AuthTests_".Length..], "N", out _))
            throw new InvalidOperationException("Refusing to remove an unexpected database.");
        SqlConnection.ClearAllPools();
        var connection = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using var master = new SqlConnection(connection.ConnectionString);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText = $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}]";
        await drop.ExecuteNonQueryAsync();
    }
}

public sealed class TestDirectory : ICorporateDirectory
{
    public Guid Id { get; } = Guid.NewGuid();
    public bool Admin { get; set; } = true;
    public bool Advisor { get; set; }
    public DirectoryStatus Status { get; set; } = DirectoryStatus.Valid;
    public int Calls { get; private set; }
    public Task<DirectoryResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken) => ReadAsync(Id, cancellationToken);
    public Task<DirectoryResult> ReadAsync(Guid objectId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new DirectoryResult(Status, new(Id, "synthetic-user", "Synthetic User", Admin, Advisor)));
    }
}

public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class AuthenticationFactory : WebApplicationFactory<Program>
{
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), "GarajeMetria_AuthTests_" + Guid.NewGuid().ToString("N"));
    private readonly string? previousConfiguration;
    private readonly Action<IServiceCollection>? configureServices;
    public TestDirectory DirectoryAdapter { get; } = new();
    public TestClock Clock { get; } = new();
    public string CertificatePath => Path.Combine(temporaryDirectory, "signing.pfx");
    public string CertificatePassword { get; } = Guid.NewGuid().ToString("N");
    public string RateNamespace { get; } = "test-" + Guid.NewGuid().ToString("N");
    public string KeyPath { get; }

    public AuthenticationFactory(SqlFixture database, AuthenticationFactory? shared = null, Action<IServiceCollection>? configureServices = null)
    {
        this.configureServices = configureServices;
        KeyPath = shared?.KeyPath ?? Path.Combine(temporaryDirectory, "keys");
        CertificatePassword = shared?.CertificatePassword ?? CertificatePassword;
        RateNamespace = shared?.RateNamespace ?? RateNamespace;
        Directory.CreateDirectory(temporaryDirectory);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=GarajeMetria-Synthetic-Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        if (shared is null) File.WriteAllBytes(CertificatePath, certificate.Export(X509ContentType.Pfx, CertificatePassword));
        else File.Copy(shared.CertificatePath, CertificatePath);
        var configuration = new
        {
            FileLogging = new { DirectoryPath = Path.Combine(temporaryDirectory, "logs") },
            ConnectionStrings = new { Authentication = database.ConnectionString },
            Directory = new
            {
                Servers = new[] { "localhost" },
                Port = 636,
                SearchBase = "DC=synthetic,DC=test",
                ServiceBindDn = "CN=synthetic-service",
                ServicePassword = "synthetic-only",
                TimeoutSeconds = 5,
                AdministratorGroupDn = "CN=web_GarajeMetria_Admin,DC=synthetic,DC=test",
                AdvisorGroupDn = "CN=web_GarajeMetria_Advisor,DC=synthetic,DC=test"
            },
            Jwt = new
            {
                Issuer = "https://synthetic.test",
                AccessAudience = "synthetic-access",
                RefreshAudience = "synthetic-refresh",
                ClockSkewSeconds = 0,
                SigningCertificatePath = CertificatePath,
                SigningCertificatePassword = CertificatePassword,
                SigningKeyId = "test-key"
            },
            Http = new
            {
                AllowedOrigins = new[] { "https://localhost" },
                TrustedProxies = Array.Empty<string>(),
                DataProtectionKeyPath = KeyPath,
                DataProtectionCertificatePath = CertificatePath,
                DataProtectionCertificatePassword = CertificatePassword,
                EnableApiDocumentation = true
            },
            Authentication = new AuthenticationOptions
            {
                DefaultDomain = "TEST",
                AllowedDomains = ["TEST"],
                AccessTokenLifetimeSeconds = 600,
                RefreshTokenLifetimeSeconds = 1800,
                MaxSessionLifetimeSeconds = 28800,
                MaxUsernameLength = 256,
                MaxPasswordLength = 1024,
                MaxLoginBodyBytes = 16384,
                MaxJsonDepth = 2,
                RateLimitNamespace = RateNamespace,
                LoginIpLimit = 300,
                LoginUserLimit = 5,
                RefreshIpLimit = 600,
                RefreshUserLimit = 10,
                RateLimitWindowSeconds = 60,
                SessionRetentionDays = 30,
                RefreshRetentionHoursAfterSessionEnd = 24,
                AuditRetentionDays = 365,
                CleanupIntervalHours = 24,
                CleanupBatchSize = 100
            }
        };
        var path = Path.Combine(temporaryDirectory, "private.json");
        File.WriteAllText(path, JsonSerializer.Serialize(configuration));
        previousConfiguration = Environment.GetEnvironmentVariable("GARAJEMETRIA_PRIVATE_CONFIG");
        Environment.SetEnvironmentVariable("GARAJEMETRIA_PRIVATE_CONFIG", path);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICorporateDirectory>();
            services.AddSingleton<ICorporateDirectory>(DirectoryAdapter);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            configureServices?.Invoke(services);
        });
    }

    public HttpClient Client() => CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = true, AllowAutoRedirect = false });

    public static async Task BootstrapAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Remove(AuthenticationEndpoints.CsrfHeader);
        client.DefaultRequestHeaders.Add(AuthenticationEndpoints.CsrfHeader, json.RootElement.GetProperty("requestToken").GetString());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("GARAJEMETRIA_PRIVATE_CONFIG", previousConfiguration);
        var resolved = Path.GetFullPath(temporaryDirectory);
        if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(resolved).StartsWith("GarajeMetria_AuthTests_", StringComparison.Ordinal))
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
