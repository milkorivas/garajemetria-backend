using System.Data.Common;
using System.Net;
using System.Text.Json;
using GarajeMetria.Api.Lpr;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Users;
using GarajeMetria.Domain.Authentication;
using GarajeMetria.Domain.Lpr;
using GarajeMetria.Infrastructure.Lpr;
using GarajeMetria.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace GarajeMetria.IntegrationTests;

public sealed class LprAdministrationAndQuotaTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task AdministrativeLifecycleKeepsClientIdentityAndImmediatelyChangesVersionsAndSecretsAcrossScopes()
    {
        var actor = new Actor();
        using var factory = Factory(actor);
        using var scope = factory.Services.CreateScope();
        await AuthorizeAsync(scope.ServiceProvider, actor);
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var registered = (await sender.Send(new RegisterDeviceCommand(" Camera ", actor.Site))).Value!;
        Assert.NotNull(registered);
        Assert.Equal("Camera", registered.Device.Name);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", registered.Credentials.ClientSecret);
        Assert.Equal("lpr_" + registered.Device.Id.ToString("N"), registered.Credentials.ClientId);
        using var other = factory.Services.CreateScope();
        var verifier = other.ServiceProvider.GetRequiredService<IDeviceCredentialService>();
        Assert.True(await verifier.VerifyAsync(registered.Credentials.ClientId, registered.Credentials.ClientSecret, default));
        var before = await AuthorizationAsync(scope.ServiceProvider, registered.Device.Id);
        var renamed = (await sender.Send(new RenameDeviceCommand(registered.Device.Id, "New name", registered.Device.Version))).Value!;
        Assert.Equal(before.AuthorizationVersion, (await AuthorizationAsync(scope.ServiceProvider, registered.Device.Id)).AuthorizationVersion);
        var rotated = (await sender.Send(new RotateDeviceCredentialsCommand(registered.Device.Id, renamed.Version))).Value!;
        Assert.NotEqual(registered.Credentials.ClientSecret, rotated.Credentials.ClientSecret);
        Assert.False(await verifier.VerifyAsync(registered.Credentials.ClientId, registered.Credentials.ClientSecret, default));
        Assert.True(await verifier.VerifyAsync(registered.Credentials.ClientId, rotated.Credentials.ClientSecret, default));
        Assert.NotEqual(before.AuthorizationVersion, (await AuthorizationAsync(scope.ServiceProvider, registered.Device.Id)).AuthorizationVersion);
        var revoked = (await sender.Send(new RevokeDeviceCredentialsCommand(registered.Device.Id, rotated.Version))).Value!;
        Assert.False(await verifier.VerifyAsync(registered.Credentials.ClientId, rotated.Credentials.ClientSecret, default));
        await using var db = database.CreateContext();
        var application = await db.Set<OpenIddictEntityFrameworkCoreApplication>().SingleAsync(x => x.Id == registered.Device.Id.ToString("N"));
        Assert.Null(application.ClientSecret);
        Assert.Equal("confidential", application.ClientType);
        Assert.Equal("[]", application.Permissions);
        var deactivated = (await sender.Send(new DeactivateDeviceCommand(registered.Device.Id, revoked.Version))).Value!;
        var reactivated = (await sender.Send(new ReactivateDeviceCommand(registered.Device.Id, deactivated.Version))).Value!;
        Assert.Equal(registered.Device.Id, reactivated.Device.Id);
        Assert.Equal(actor.Site, reactivated.Device.WorkshopId);
        Assert.True(await verifier.VerifyAsync(registered.Credentials.ClientId, reactivated.Credentials.ClientSecret, default));
        Assert.False(await verifier.VerifyAsync(registered.Credentials.ClientId, registered.Credentials.ClientSecret, default));
        Assert.Equal(1, await db.Set<OpenIddictEntityFrameworkCoreApplication>().CountAsync(x => x.Id == registered.Device.Id.ToString("N")));
        Assert.Equal(6, await db.DeviceAdministrationAudits.CountAsync(x => x.DeviceId == registered.Device.Id));
        var page = (await sender.Send(new ListDevicesQuery(1, 20, actor.Site))).Value!;
        Assert.Single(page.Items);
        Assert.DoesNotContain(reactivated.Credentials.ClientSecret, JsonSerializer.Serialize(page));
    }

    [SqlTheory]
    [InlineData("audit")]
    [InlineData("manager")]
    public async Task FailedRotationRestoresSecretDeviceVersionAndAudit(string failure)
    {
        var actor = new Actor();
        var fault = new CredentialFault { FailManager = failure == "manager" };
        using var factory = Factory(actor, services =>
        {
            services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(fault));
            if (failure == "audit")
            {
                services.RemoveAll<IDeviceAdministrationAuditRepository>();
                services.AddScoped<IDeviceAdministrationAuditRepository>(sp => new FaultyAudit(sp.GetRequiredService<AuthenticationDbContext>(), fault));
            }
        });
        using var scope = factory.Services.CreateScope();
        await AuthorizeAsync(scope.ServiceProvider, actor);
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var created = (await sender.Send(new RegisterDeviceCommand("Camera", actor.Site))).Value!;
        fault.Enabled = true;
        var exception = await Assert.ThrowsAsync<PersistenceFailureException>(() => sender.Send(new RotateDeviceCredentialsCommand(created.Device.Id, created.Device.Version)));
        Assert.Equal(FailureCode.InternalError, exception.Code);
        fault.Enabled = false;
        using var verifyScope = factory.Services.CreateScope();
        var verifier = verifyScope.ServiceProvider.GetRequiredService<IDeviceCredentialService>();
        Assert.True(await verifier.VerifyAsync(created.Credentials.ClientId, created.Credentials.ClientSecret, default));
        var device = (await verifyScope.ServiceProvider.GetRequiredService<ILprDeviceRepository>().FindAsync(created.Device.Id, default))!;
        Assert.Equal(created.Device.Version, device.Version);
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.DeviceAdministrationAudits.CountAsync(x => x.DeviceId == device.Id));
    }

    [SqlFact]
    public async Task NoOpStaleVersionAndMissingCatalogDoNotGenerateSecretsOrExtraAudits()
    {
        var actor = new Actor();
        using var factory = Factory(actor);
        using var scope = factory.Services.CreateScope();
        await AuthorizeAsync(scope.ServiceProvider, actor);
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var created = (await sender.Send(new RegisterDeviceCommand("Camera", actor.Site))).Value!;
        Assert.Equal(FailureCode.PreconditionFailed, (await sender.Send(new RenameDeviceCommand(created.Device.Id, "Camera", Guid.NewGuid()))).Error?.Code);
        Assert.Equal(created.Device.Version, (await sender.Send(new RenameDeviceCommand(created.Device.Id, "Camera", created.Device.Version))).Value!.Version);
        await using var db = database.CreateContext();
        await db.Workshops.Where(x => x.Id == actor.Site).ExecuteUpdateAsync(x => x.SetProperty(w => w.IsActive, false));
        Assert.Equal(FailureCode.DeviceConflict, (await sender.Send(new RotateDeviceCredentialsCommand(created.Device.Id, created.Device.Version))).Error?.Code);
        Assert.True(await scope.ServiceProvider.GetRequiredService<IDeviceCredentialService>().VerifyAsync(created.Credentials.ClientId, created.Credentials.ClientSecret, default));
        Assert.Equal(1, await db.DeviceAdministrationAudits.CountAsync(x => x.DeviceId == created.Device.Id));
    }

    [SqlFact]
    public async Task QuotasAreSharedByTwoHostsAndIsolatedFromUserAuthenticationPolicies()
    {
        using var first = new AuthenticationFactory(database);
        using var second = new AuthenticationFactory(database, shared: first);
        using var firstScope = first.Services.CreateScope();
        using var secondScope = second.Services.CreateScope();
        var firstOptions = firstScope.ServiceProvider.GetRequiredService<LprOptions>();
        var secondOptions = secondScope.ServiceProvider.GetRequiredService<LprOptions>();
        firstOptions.EventsPerDevice = secondOptions.EventsPerDevice = 1;
        var policy = firstOptions.Policy("lpr:events-device");
        var subject = Guid.NewGuid().ToString("N");
        Assert.True((await firstScope.ServiceProvider.GetRequiredService<ILprRateLimitStore>().AdmitAsync(policy, subject, default)).Allowed);
        var rejected = await secondScope.ServiceProvider.GetRequiredService<ILprRateLimitStore>().AdmitAsync(policy, subject, default);
        Assert.False(rejected.Allowed);
        Assert.InRange(rejected.RetryAfterSeconds, 1, 60);
        Assert.True((await secondScope.ServiceProvider.GetRequiredService<ILprRateLimitStore>().AdmitAsync(secondOptions.Policy("lpr:tokens-client"), subject, default)).Allowed);
        Assert.Equal(0, first.DirectoryAdapter.Calls);
        Assert.Equal(0, second.DirectoryAdapter.Calls);
    }

    [SqlFact]
    public async Task ExactQuotaWindowMismatchAndUnavailableSqlFailClosed()
    {
        using var factory = new AuthenticationFactory(database);
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<LprOptions>();
        options.EventsPerDevice = 1;
        var oauth = scope.ServiceProvider.GetRequiredService<DeviceOAuthOptions>();
        var certificates = scope.ServiceProvider.GetRequiredService<DeviceOAuthCertificates>();
        var time = new FixedClock();
        await using var db = database.CreateContext();
        var store = new LprRateLimitStore(db, options, oauth, certificates, time, NullLogger<LprRateLimitStore>.Instance);
        var policy = options.Policy("lpr:events-device");
        Assert.True((await store.AdmitAsync(policy, "synthetic-device", default)).Allowed);
        time.Now = time.Now.AddSeconds(60).AddTicks(-1);
        Assert.False((await store.AdmitAsync(policy, "synthetic-device", default)).Allowed);
        time.Now = time.Now.AddTicks(1);
        Assert.True((await store.AdmitAsync(policy, "synthetic-device", default)).Allowed);
        options.EventsPerDevice = 2;
        Assert.Equal(FailureCode.ServiceUnavailable, (await Assert.ThrowsAsync<PersistenceFailureException>(() => store.AdmitAsync(options.Policy("lpr:events-device"), "synthetic-device", default))).Code);
        await using var broken = new AuthenticationDbContext(new DbContextOptionsBuilder<AuthenticationDbContext>().UseSqlServer(database.ConnectionString).AddInterceptors(new SqlFault()).Options);
        var unavailable = new LprRateLimitStore(broken, options, oauth, certificates, time, NullLogger<LprRateLimitStore>.Instance);
        Assert.Equal(FailureCode.ServiceUnavailable, (await Assert.ThrowsAsync<PersistenceFailureException>(() => unavailable.AdmitAsync(options.Policy("lpr:events-device"), "other", default))).Code);
    }

    private AuthenticationFactory Factory(Actor actor, Action<IServiceCollection>? configure = null) => new(database, configureServices: services =>
    {
        services.RemoveAll<ICurrentUserContext>();
        services.AddSingleton<ICurrentUserContext>(actor);
        configure?.Invoke(services);
    });

    [SqlFact]
    public async Task SharedCleanupDoesNotDeletePermitsInsideTheLongerLprWindow()
    {
        using var factory = new AuthenticationFactory(database);
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<LprOptions>();
        options.RateLimitWindowSeconds = 120;
        var auth = scope.ServiceProvider.GetRequiredService<AuthenticationOptions>();
        await using var db = database.CreateContext();
        var id = Guid.NewGuid();
        var partition = id.ToString("N");
        var now = await SqlTransactions.NowAsync(db, default);
        db.RatePartitions.Add(new() { Id = partition });
        db.RatePermits.Add(new() { Id = id, PartitionId = partition, AcceptedAt = now.AddSeconds(-90) });
        await db.SaveChangesAsync();
        await db.CleanupSchedules.ExecuteDeleteAsync();
        var cleaner = new RetentionStore(db, auth, NullLogger<RetentionStore>.Instance, options);
        Assert.True((await cleaner.CleanBatchAsync("synthetic-cleaner", auth.Retention, default)).Acquired);
        Assert.True(await db.RatePermits.AnyAsync(x => x.Id == id));
    }
    private async Task AuthorizeAsync(IServiceProvider provider, Actor actor)
    {
        var login = (await provider.GetRequiredService<ISender>().Send(new LoginCommand("user", "synthetic"))).Value!;
        await using var db = database.CreateContext();
        var userId = Guid.Parse(login.User!.Id);
        actor.User = new(userId, (await db.Sessions.AsNoTracking().SingleAsync(x => x.UserId == userId && x.ClosedAt == null)).Id, UserRoles.Administrator);
        db.Workshops.Add(new() { Id = actor.Site, Code = actor.Site, Name = "Synthetic", IsActive = true });
        if (!await db.Catalog.AnyAsync()) db.Catalog.Add(new() { Id = 1, LastPublishedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }
    private static async Task<DeviceAuthorizationState> AuthorizationAsync(IServiceProvider provider, Guid id) =>
        (await provider.GetRequiredService<IDeviceAuthorizationQuery>().FindAsync(id, default))!;
    private sealed class Actor : ICurrentUserContext
    {
        public string Site { get; } = "site-" + Guid.NewGuid().ToString("N");
        public CurrentUser? User { get; set; }
        public string CorrelationId => "synthetic-correlation";
    }
    private sealed class FixedClock : ILprClock
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public Task<DateTimeOffset> GetUtcNowAsync(CancellationToken ct) => Task.FromResult(Now);
    }
    private sealed class FaultyAudit(AuthenticationDbContext db, CredentialFault fault) : IDeviceAdministrationAuditRepository
    {
        public void Add(DeviceAdministrationAuditEntry entry)
        {
            if (fault.Enabled) throw new InvalidOperationException("Synthetic audit failure");
            new LprRepository(db).Add(entry);
        }
    }
    private sealed class CredentialFault : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public bool FailManager { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (Enabled && FailManager && command.CommandText.Contains("UPDATE [OpenIddictApplications]", StringComparison.Ordinal))
                throw new InvalidOperationException("Synthetic credential manager failure");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class SqlFault : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData data,
            InterceptionResult result, CancellationToken ct = default) => throw new PersistenceFailureException(FailureCode.ServiceUnavailable);
    }
}

public sealed class LprQuotaTransportTests
{
    [Fact]
    public async Task AdministrativeRequestTimeoutRestoresCancellationAndUsesProblemDetailsWithoutIpQuota()
    {
        using var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new LprTrafficMetadata(LprTraffic.Administration)), "synthetic"));
        var quota = new RejectQuota(false);
        await new LprIpRateLimitMiddleware(c => Task.Delay(Timeout.Infinite, c.RequestAborted))
            .InvokeAsync(context, quota, new() { RequestTimeoutSeconds = 2, LockTimeoutSeconds = 1 }, NullLogger<LprIpRateLimitMiddleware>.Instance);
        Assert.Equal(503, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType);
        Assert.Null(quota.Subject);
        Assert.False(context.RequestAborted.IsCancellationRequested);
    }

    [Fact]
    public async Task UnexpectedTokenQuotaFailureStillUsesOAuthErrorWithoutPrivateDetails()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        Assert.False(await LprRateLimits.AdmitAsync(context, new RejectQuota(false, unexpected: true), new(), LprTraffic.Token,
            "lpr:tokens-client", "synthetic-client"));
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Equal(500, context.Response.StatusCode);
        Assert.Contains("server_error", text);
        Assert.DoesNotContain("synthetic-private", text);
    }
    [Fact]
    public async Task IpGateUsesRemoteAddressAndStopsBeforeAuthenticationOrHashing()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.1";
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new LprTrafficMetadata(LprTraffic.Token)), "synthetic"));
        var quota = new RejectQuota(false);
        var reached = false;
        await new LprIpRateLimitMiddleware(_ => { reached = true; return Task.CompletedTask; })
            .InvokeAsync(context, quota, new(), NullLogger<LprIpRateLimitMiddleware>.Instance);
        Assert.False(reached);
        Assert.Equal("127.0.0.1", quota.Subject);
        Assert.Equal("lpr:tokens-ip", quota.Policy);
        Assert.Equal(429, context.Response.StatusCode);
    }
    [Theory]
    [InlineData(LprTraffic.Token, false, 429, "application/json")]
    [InlineData(LprTraffic.Event, false, 429, "application/problem+json")]
    [InlineData(LprTraffic.Token, true, 503, "application/json")]
    [InlineData(LprTraffic.Event, true, 503, "application/problem+json")]
    public async Task QuotaErrorsRespectOAuthOrProblemDetailsWithoutLeakingSubjects(LprTraffic traffic, bool failure, int status, string contentType)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        using (services)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Response.Body = new MemoryStream();
            Assert.False(await LprRateLimits.AdmitAsync(context, new RejectQuota(failure), new(), traffic,
                traffic == LprTraffic.Token ? "lpr:tokens-ip" : "lpr:events-ip", "private-subject"));
            Assert.Equal(status, context.Response.StatusCode);
            Assert.StartsWith(contentType, context.Response.ContentType);
            Assert.Equal("no-store", context.Response.Headers.CacheControl);
            if (!failure) Assert.Equal("2", context.Response.Headers.RetryAfter);
            context.Response.Body.Position = 0;
            var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
            Assert.DoesNotContain("private-subject", text);
            if (traffic == LprTraffic.Token) Assert.Contains("temporarily_unavailable", text);
        }
    }
    private sealed class RejectQuota(bool failure, bool unexpected = false) : ILprRateLimitStore
    {
        public string? Subject { get; private set; }
        public string? Policy { get; private set; }
        public Task<RateDecision> AdmitAsync(RatePolicy policy, string subject, CancellationToken ct)
        {
            Subject = subject;
            Policy = policy.Name;
            if (unexpected) throw new InvalidOperationException("synthetic-private-error");
            return failure ? throw new PersistenceFailureException(FailureCode.ServiceUnavailable) :
                Task.FromResult(new RateDecision(false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMilliseconds(1100)));
        }
    }
}
