using System.Data.Common;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Users;
using GarajeMetria.Application.Workshops;
using GarajeMetria.Domain.Authentication;
using GarajeMetria.Domain.Lpr;
using GarajeMetria.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.EntityFrameworkCore.Models;

namespace GarajeMetria.IntegrationTests;

public sealed partial class LprIngestionTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task IngestionPreservesInvalidAndLowConfidenceReadingsAndConfirmsOldSemanticRetries()
    {
        var actor = new Actor();
        var clock = new Clock();
        using var factory = Factory(actor, clock);
        var device = await SeedAsync(factory, actor);
        var request = Request(device, clock.Now);
        var original = await SendAsync(factory, request);
        Assert.True(original.Succeeded);
        Assert.False(original.Value!.Duplicate);
        Assert.Equal("invalid", original.Value.Receipt.PlateStatus);
        clock.Now = clock.Now.AddHours(2);
        var duplicate = await SendAsync(factory, request with { CapturedAt = request.CapturedAt.ToOffset(TimeSpan.FromHours(1)), Confidence = 0.00000000m });
        Assert.Equal(original.Value.Receipt, duplicate.Value!.Receipt);
        Assert.True(duplicate.Value.Duplicate);
        foreach (var changed in new[] { request with { PlateOriginal = "SYNTHETIC-INVALID" }, request with { Confidence = 0.1m },
                     request with { CapturedAt = request.CapturedAt.AddTicks(1) }, request with { Direction = LprDirection.Entry } })
            Assert.Equal(FailureCode.EventConflict, (await SendAsync(factory, changed)).Error?.Code);
        Assert.Equal(FailureCode.CaptureOutsideWindow, (await SendAsync(factory, request with { EventId = Guid.NewGuid() })).Error?.Code);
        await using var db = database.CreateContext();
        var saved = await db.LprEvents.SingleAsync(x => x.EventId == request.EventId);
        Assert.Equal("synthetic-invalid", saved.PlateOriginal);
        Assert.Equal(0m, saved.Confidence);
        Assert.Equal(clock.Now.AddHours(-2), saved.ReceivedAt);
        Assert.Null(saved.Direction);
        Assert.Equal(1, await db.LprEvents.CountAsync(x => x.DeviceId == device.Device.Id));
    }

    [SqlFact]
    public async Task ConcurrentHostsDeduplicateOneUuidButPreserveDifferentUuids()
    {
        var actor = new Actor();
        var clock = new Clock();
        using var first = Factory(actor, clock);
        var device = await SeedAsync(first, actor);
        using var second = Factory(actor, clock, shared: first);
        var request = Request(device, clock.Now);
        var results = await Task.WhenAll(SendAsync(first, request), SendAsync(second, request));
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Single(results, x => x.Value!.Duplicate);
        Assert.Equal(results[0].Value!.Receipt, results[1].Value!.Receipt);
        Assert.False((await SendAsync(second, request with { EventId = Guid.NewGuid() })).Value!.Duplicate);
        await using var db = database.CreateContext();
        Assert.Equal(2, await db.LprEvents.CountAsync(x => x.DeviceId == device.Device.Id));
    }

    [SqlFact]
    public async Task ForeignUuidConflictsWithoutLeakingTheReceiptAndRevokedDevicesCannotConfirmDuplicates()
    {
        var actor = new Actor();
        var clock = new Clock();
        using var factory = Factory(actor, clock);
        var first = await SeedAsync(factory, actor);
        var identity = actor.Device;
        var request = Request(first, clock.Now);
        Assert.True((await SendAsync(factory, request)).Succeeded);
        var second = await SeedAsync(factory, actor, login: false);
        var conflict = await SendAsync(factory, Request(second, clock.Now) with { EventId = request.EventId });
        Assert.Equal(FailureCode.EventConflict, conflict.Error?.Code);
        Assert.Null(conflict.Value);
        actor.Device = identity;
        using var scope = factory.Services.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RevokeDeviceCredentialsCommand(first.Device.Id, first.Device.Version))).Succeeded);
        Assert.Equal(FailureCode.InvalidDeviceToken, (await SendAsync(factory, request)).Error?.Code);
    }

    [SqlFact]
    public async Task ARealPrimaryKeyCollisionRollsBackAndReconcilesWithoutRepeatingTheWrite()
    {
        var actor = new Actor();
        var clock = new Clock();
        var miss = new MissOnce();
        using var factory = Factory(actor, clock, services =>
        {
            services.RemoveAll<ILprEventRepository>();
            services.AddScoped<ILprEventRepository>(sp => new MissingLookup(new LprRepository(sp.GetRequiredService<AuthenticationDbContext>()), miss));
        });
        var device = await SeedAsync(factory, actor);
        var request = Request(device, clock.Now);
        var first = await SendAsync(factory, request);
        miss.Enabled = true;
        var reconciled = await SendAsync(factory, request);
        Assert.True(reconciled.Succeeded);
        Assert.True(reconciled.Value!.Duplicate);
        Assert.Equal(first.Value!.Receipt, reconciled.Value.Receipt);
        Assert.Equal(2, miss.Insertions);
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.LprEvents.CountAsync(x => x.EventId == request.EventId));
    }

    [SqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrUnacknowledgedCommitNeverReturnsSuccessAndAnExplicitRetryReconciles(bool loseAcknowledgment)
    {
        var actor = new Actor();
        var clock = new Clock();
        var fault = new CommitFault { AfterCommit = loseAcknowledgment };
        using var factory = Factory(actor, clock, services => services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(fault, new BeforeSaveFault(fault))));
        var device = await SeedAsync(factory, actor);
        var request = Request(device, clock.Now);
        fault.Enabled = true;
        var failure = await Assert.ThrowsAsync<PersistenceFailureException>(() => SendAsync(factory, request));
        Assert.Equal(loseAcknowledgment ? FailureCode.OperationOutcomeUnknown : FailureCode.InternalError, failure.Code);
        await using var db = database.CreateContext();
        Assert.Equal(loseAcknowledgment, await db.LprEvents.AnyAsync(x => x.EventId == request.EventId));
        fault.Enabled = false;
        var retry = await SendAsync(factory, request);
        Assert.True(retry.Succeeded);
        Assert.Equal(loseAcknowledgment, retry.Value!.Duplicate);
    }

    [SqlFact]
    public async Task CatalogDeactivationInvalidatesEveryDeviceAtomicallyWithoutChangingItsCredentialsOrActivity()
    {
        var actor = new Actor();
        var clock = new Clock();
        using var factory = Factory(actor, clock);
        var first = await SeedAsync(factory, actor);
        var oldIdentity = actor.Device;
        using var scope = factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var other = (await sender.Send(new RegisterDeviceCommand("Inactive camera", first.Device.WorkshopId))).Value!;
        await sender.Send(new DeactivateDeviceCommand(other.Device.Id, other.Device.Version));
        var unaffected = await SeedAsync(factory, actor, login: false);
        var unaffectedVersion = actor.Device!.AuthorizationVersion;
        await using var db = database.CreateContext();
        var before = await db.LprDevices.AsNoTracking().Where(x => x.WorkshopId == first.Device.WorkshopId).ToArrayAsync();
        var hash = (await db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking().SingleAsync(x => x.Id == first.Device.Id.ToString("N"))).ClientSecret;
        var store = scope.ServiceProvider.GetRequiredService<IWorkshopCatalogStore>();
        await store.PublishAsync(await store.BeginObservationAsync(default), [new(unaffected.Device.WorkshopId, "Unchanged")], default);
        var after = await db.LprDevices.AsNoTracking().Where(x => x.WorkshopId == first.Device.WorkshopId).ToArrayAsync();
        foreach (var original in before)
        {
            var current = after.Single(x => x.Id == original.Id);
            Assert.NotEqual(original.AuthorizationVersion, current.AuthorizationVersion);
            Assert.NotEqual(original.Version, current.Version);
            Assert.Equal(original.IsActive, current.IsActive);
            Assert.Equal(original.HasCredentials, current.HasCredentials);
            Assert.Equal(original.WorkshopId, current.WorkshopId);
        }
        Assert.Equal(unaffectedVersion, (await db.LprDevices.AsNoTracking().SingleAsync(x => x.Id == unaffected.Device.Id)).AuthorizationVersion);
        await store.PublishAsync(await store.BeginObservationAsync(default), [new(first.Device.WorkshopId, "Restored"), new(unaffected.Device.WorkshopId, "Unchanged")], default);
        actor.Device = oldIdentity;
        Assert.Equal(FailureCode.InvalidDeviceToken, (await sender.Send(new ValidateDeviceAccessQuery(first.Device.Id, first.Device.WorkshopId))).Error?.Code);
        var grant = await sender.Send(new PrepareDeviceTokenGrantCommand(first.Credentials.ClientId, first.Credentials.ClientSecret));
        Assert.True(grant.Succeeded);
        Assert.NotEqual(oldIdentity!.AuthorizationVersion, grant.Value!.AuthorizationVersion);
        Assert.Equal(hash, (await db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking().SingleAsync(x => x.Id == first.Device.Id.ToString("N"))).ClientSecret);
        Assert.False((await db.LprDevices.AsNoTracking().SingleAsync(x => x.Id == other.Device.Id)).IsActive);
    }

    [SqlFact]
    public async Task PublicationFailureRollsBackWorkshopDeviceVersionsAndPublicationMarker()
    {
        var actor = new Actor();
        var clock = new Clock();
        var fault = new PublicationFault();
        using var factory = Factory(actor, clock, services => services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(fault)));
        var device = await SeedAsync(factory, actor);
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkshopCatalogStore>();
        var observation = await store.BeginObservationAsync(default);
        await using var db = database.CreateContext();
        var before = await db.Catalog.AsNoTracking().SingleAsync();
        fault.Enabled = true;
        await Assert.ThrowsAsync<PersistenceFailureException>(() => store.PublishAsync(observation, [], default));
        Assert.True((await db.Workshops.AsNoTracking().SingleAsync(x => x.Id == device.Device.WorkshopId)).IsActive);
        Assert.Equal(actor.Device!.AuthorizationVersion, (await db.LprDevices.AsNoTracking().SingleAsync(x => x.Id == device.Device.Id)).AuthorizationVersion);
        Assert.Equal(before.PublishedVersion, (await db.Catalog.AsNoTracking().SingleAsync()).PublishedVersion);
        Assert.Equal(before.LastPublishedAt, (await db.Catalog.AsNoTracking().SingleAsync()).LastPublishedAt);
    }

    [SqlFact]
    public async Task MissingInitialCatalogRejectsEvenDuplicatesWhileAnOldConfirmedCopyRemainsUsable()
    {
        var actor = new Actor();
        var clock = new Clock();
        using var factory = Factory(actor, clock);
        var device = await SeedAsync(factory, actor);
        var request = Request(device, clock.Now);
        Assert.True((await SendAsync(factory, request)).Succeeded);
        await using var db = database.CreateContext();
        var previous = (await db.Catalog.AsNoTracking().SingleAsync()).LastPublishedAt;
        try
        {
            await db.Catalog.ExecuteUpdateAsync(x => x.SetProperty(c => c.LastPublishedAt, (DateTimeOffset?)null));
            Assert.Equal(FailureCode.ServiceUnavailable, (await SendAsync(factory, request)).Error?.Code);
            await db.Catalog.ExecuteUpdateAsync(x => x.SetProperty(c => c.LastPublishedAt, clock.Now.AddYears(-1)));
            Assert.True((await SendAsync(factory, request)).Value!.Duplicate);
        }
        finally { await db.Catalog.ExecuteUpdateAsync(x => x.SetProperty(c => c.LastPublishedAt, previous)); }
    }

    private AuthenticationFactory Factory(Actor actor, Clock clock, Action<IServiceCollection>? configure = null, AuthenticationFactory? shared = null) =>
        new(database, shared: shared, configureServices: services =>
        {
            services.RemoveAll<ICurrentUserContext>(); services.AddSingleton<ICurrentUserContext>(actor);
            services.RemoveAll<ICurrentDeviceContext>(); services.AddSingleton<ICurrentDeviceContext>(actor);
            services.RemoveAll<ILprClock>(); services.AddSingleton<ILprClock>(clock);
            configure?.Invoke(services);
        });
    private async Task<DeviceProvisioningResult> SeedAsync(AuthenticationFactory factory, Actor actor, bool login = true)
    {
        using var scope = factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        if (login)
        {
            var authentication = (await sender.Send(new LoginCommand("user", "synthetic"))).Value!;
            var id = Guid.Parse(authentication.User!.Id);
            await using var userDb = database.CreateContext();
            actor.User = new(id, (await userDb.Sessions.AsNoTracking().SingleAsync(x => x.UserId == id && x.ClosedAt == null)).Id, UserRoles.Administrator);
        }
        var site = "ingestion-" + Guid.NewGuid().ToString("N");
        await using var db = database.CreateContext();
        db.Workshops.Add(new() { Id = site, Code = site, Name = "Synthetic", IsActive = true });
        if (!await db.Catalog.AnyAsync()) db.Catalog.Add(new() { Id = 1, LastPublishedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var result = await sender.Send(new RegisterDeviceCommand("Synthetic camera", site));
        Assert.True(result.Succeeded);
        var state = await scope.ServiceProvider.GetRequiredService<IDeviceAuthorizationQuery>().FindAsync(result.Value!.Device.Id, default);
        actor.Device = new(state!.DeviceId, site, state.AuthorizationVersion, DateTimeOffset.UtcNow.AddDays(1), true);
        return result.Value;
    }
    private static ReceiveLprEventCommand Request(DeviceProvisioningResult device, DateTimeOffset capture) =>
        new(Guid.NewGuid(), device.Device.Id, device.Device.WorkshopId, capture, "synthetic-invalid", 0m);
    private static async Task<Outcome<ReceiveLprEventResult>> SendAsync(AuthenticationFactory factory, ReceiveLprEventCommand request)
    { using var scope = factory.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request); }
    private sealed class Actor : ICurrentUserContext, ICurrentDeviceContext
    {
        public CurrentUser? User { get; set; }
        public CurrentDevice? Device { get; set; }
        public string CorrelationId => "synthetic-ingestion";
    }
    private sealed class Clock : ILprClock
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public Task<DateTimeOffset> GetUtcNowAsync(CancellationToken ct) => Task.FromResult(Now);
    }
    private sealed class MissOnce { public bool Enabled { get; set; } public int Insertions { get; set; } }
    private sealed class MissingLookup(ILprEventRepository inner, MissOnce miss) : ILprEventRepository
    {
        public Task<LprEventLookup> CompareAsync(ReceiveLprEventCommand incoming, CancellationToken ct)
        { if (!miss.Enabled) return inner.CompareAsync(incoming, ct); miss.Enabled = false; return Task.FromResult(new LprEventLookup(LprEventComparison.Missing)); }
        public void Add(LprEvent observation) { miss.Insertions++; inner.Add(observation); }
    }
    private sealed class CommitFault : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public bool AfterCommit { get; set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default) =>
            Enabled && AfterCommit && data.Context!.ChangeTracker.Entries<LprEventRow>().Any()
                ? throw new InvalidOperationException("Synthetic lost acknowledgment") : Task.CompletedTask;
    }
    private sealed class BeforeSaveFault(CommitFault fault) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default) =>
            fault.Enabled && !fault.AfterCommit && data.Context!.ChangeTracker.Entries<LprEventRow>().Any()
                ? throw new InvalidOperationException("Synthetic save failure") : ValueTask.FromResult(result);
    }
    private sealed class PublicationFault : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default) => Enabled && command.CommandText.Contains("UPDATE [LprDevices]", StringComparison.Ordinal)
                ? throw new InvalidOperationException("Synthetic publication failure") : ValueTask.FromResult(result);
    }
}
