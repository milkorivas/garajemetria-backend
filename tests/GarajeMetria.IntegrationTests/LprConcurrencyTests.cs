using System.Data.Common;
using System.Net;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Workshops;
using GarajeMetria.Domain.Workshops;
using GarajeMetria.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.EntityFrameworkCore.Models;

namespace GarajeMetria.IntegrationTests;

public sealed partial class LprHttpTests
{
    [SqlFact]
    public async Task OverlappingHttpDeliveriesWaitOnTheRealCatalogLockAndProduceOneEvent()
    {
        var gate = new SaveGate(events: true);
        var wait = new CatalogWait();
        using var first = Factory(configure: services => services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(gate)));
        var setup = await SetupAsync(first);
        using var second = Factory(first, services => services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(wait)));
        using var admin = Client(first, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var oauth = first.Client();
        var token = (await ReadAsync(await TokenAsync(oauth, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await ProbeAsync(second, token)).StatusCode);
        using var clientA = Client(first, token);
        using var clientB = Client(second, token);
        var id = Guid.NewGuid();
        var body = EventBody(id, device.Id, setup.Site, DateTimeOffset.UtcNow);
        gate.Enabled = true;
        var a = PostEventAsync(clientA, body);
        Task<HttpResponseMessage>? b = null;
        try
        {
            await gate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            wait.Enabled = true;
            b = PostEventAsync(clientB, body);
            await wait.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(b.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }
        var responses = await Task.WhenAll(a, b!);
        Assert.Equal(HttpStatusCode.Created, responses[0].StatusCode);
        Assert.Equal(HttpStatusCode.OK, responses[1].StatusCode);
        Assert.Equal((await ReadAsync(responses[0])).GetProperty("received_at").GetString(), (await ReadAsync(responses[1])).GetProperty("received_at").GetString());
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.LprEvents.CountAsync(x => x.EventId == id));
        Assert.Equal(0, second.DirectoryAdapter.Calls);
    }

    [SqlFact]
    public async Task TokenExpiringWhileWaitingForTheCatalogLockCannotPersistAnEvent()
    {
        var clock = new EventClock();
        var wait = new CatalogWait();
        using var factory = Factory(configure: services =>
        {
            services.RemoveAll<ILprClock>(); services.AddSingleton<ILprClock>(clock);
            services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(wait));
        });
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var oauth = factory.Client();
        var token = (await ReadAsync(await TokenAsync(oauth, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await ProbeAsync(factory, token)).StatusCode);
        using var client = Client(factory, token);
        await using var holder = database.CreateContext();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await SqlTransactions.LockAsync(holder, "workshops:catalog", default);
        var id = Guid.NewGuid();
        wait.Enabled = true;
        var pending = PostEventAsync(client, EventBody(id, device.Id, setup.Site, clock.Now));
        try
        {
            await wait.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(pending.IsCompleted);
            clock.Now = clock.Now.AddMinutes(15);
        }
        finally { await transaction.CommitAsync(); }
        Assert.Equal(HttpStatusCode.Unauthorized, (await pending).StatusCode);
        Assert.False(await holder.LprEvents.AnyAsync(x => x.EventId == id));
    }

    [SqlTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CatalogPublicationAndIngestionRespectTheCommitOrderAndNeverReviveOldTokens(bool eventFirst)
    {
        var gate = new SaveGate(events: eventFirst);
        var wait = new CatalogWait();
        using var first = Factory(configure: services => services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(gate)));
        var setup = await SetupAsync(first);
        using var second = Factory(first, services => services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(wait)));
        using var admin = Client(first, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var oauth = first.Client();
        var token = (await ReadAsync(await TokenAsync(oauth, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await ProbeAsync(second, token)).StatusCode);
        using var client = Client(eventFirst ? first : second, token);
        using var observationScope = first.Services.CreateScope();
        var observation = await observationScope.ServiceProvider.GetRequiredService<IWorkshopCatalogStore>().BeginObservationAsync(default);
        var id = Guid.NewGuid();
        var body = EventBody(id, device.Id, setup.Site, DateTimeOffset.UtcNow);
        gate.Enabled = true;
        Task<HttpResponseMessage>? delivery = eventFirst ? PostEventAsync(client, body) : null;
        Task<CatalogPublication>? publication = eventFirst ? null : PublishAsync(first, observation, []);
        try
        {
            await gate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            wait.Enabled = true;
            if (eventFirst) publication = PublishAsync(second, observation, []);
            else delivery = PostEventAsync(client, body);
            await wait.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(eventFirst ? publication!.IsCompleted : delivery!.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }
        var received = await delivery!;
        Assert.True((await publication!).Published);
        Assert.Equal(eventFirst ? HttpStatusCode.Created : HttpStatusCode.Unauthorized, received.StatusCode);
        await using var db = database.CreateContext();
        Assert.Equal(eventFirst, await db.LprEvents.AnyAsync(x => x.EventId == id));
        await PublishAsync(second, await observationScope.ServiceProvider.GetRequiredService<IWorkshopCatalogStore>().BeginObservationAsync(default), [new(setup.Site, "Restored")]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostEventAsync(client, body)).StatusCode);
        var freshReply = await TokenAsync(oauth, device.ClientId, device.Secret);
        Assert.Equal(HttpStatusCode.OK, freshReply.StatusCode);
    }

    private static async Task<CatalogPublication> PublishAsync(AuthenticationFactory factory, CatalogObservation observation, IReadOnlyList<WorkshopEntry> entries)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkshopCatalogStore>().PublishAsync(observation, entries, default);
    }
    [SqlFact]
    public async Task UnacknowledgedRotationIsRecoveredThroughFreshEtagAndAnotherRotationWithoutRecoveringTheLostSecret()
    {
        var fault = new LostRotationCommit();
        using var factory = Factory(configure: services => services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(fault)));
        var setup = await SetupAsync(factory);
        using var admin = Client(factory, setup.Access);
        var device = await CreateAsync(admin, setup.Site);
        using var oauth = factory.Client();
        var oldToken = (await ReadAsync(await TokenAsync(oauth, device.ClientId, device.Secret))).GetProperty("access_token").GetString()!;
        fault.Enabled = true;
        var unknown = await MutationAsync(admin, device.Id, "credentials/rotate", device.Tag);
        Assert.Equal(HttpStatusCode.InternalServerError, unknown.StatusCode);
        var problem = await ReadAsync(unknown);
        Assert.Equal("operation_outcome_unknown", problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("credentials", out _));
        fault.Enabled = false;
        var detail = await admin.GetAsync("/api/lpr/devices/" + device.Id);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.False((await ReadAsync(detail)).TryGetProperty("credentials", out _));
        Assert.NotEqual(device.Tag, detail.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await MutationAsync(admin, device.Id, "credentials/rotate", device.Tag)).StatusCode);
        await using var db = database.CreateContext();
        var lostHash = (await db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking().SingleAsync(x => x.Id == device.Id.ToString("N"))).ClientSecret;
        var recovered = await MutationAsync(admin, device.Id, "credentials/rotate", detail.Headers.ETag.ToString());
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var newSecret = (await ReadAsync(recovered)).GetProperty("credentials").GetProperty("clientSecret").GetString()!;
        Assert.NotEqual(lostHash, (await db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking().SingleAsync(x => x.Id == device.Id.ToString("N"))).ClientSecret);
        Assert.Equal(2, await db.DeviceAdministrationAudits.CountAsync(x => x.DeviceId == device.Id && x.Operation == GarajeMetria.Domain.Lpr.DeviceAdministrationOperation.CredentialsRotated));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(factory, oldToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(oauth, device.ClientId, device.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(oauth, device.ClientId, newSecret)).StatusCode);
        Assert.DoesNotContain(newSecret, ReadLogs(factory));
    }
    private sealed class LostRotationCommit : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default) =>
            Enabled && data.Context!.ChangeTracker.Entries<DeviceAdministrationAuditRow>().Any(x => x.Entity.Operation == GarajeMetria.Domain.Lpr.DeviceAdministrationOperation.CredentialsRotated)
                ? throw new IOException("Synthetic lost rotation acknowledgment") : Task.CompletedTask;
    }
    private sealed class SaveGate(bool events) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int entered;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var relevant = events ? data.Context!.ChangeTracker.Entries<LprEventRow>().Any(x => x.State == EntityState.Added) :
                data.Context!.ChangeTracker.Entries<GarajeMetria.Domain.Lpr.LprDevice>().Any(x => x.State == EntityState.Modified);
            if (Enabled && relevant && Interlocked.CompareExchange(ref entered, 1, 0) == 0)
            { Arrived.TrySetResult(); await Release.Task.WaitAsync(ct); }
            return result;
        }
    }
    private sealed class CatalogWait : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && command.CommandText.Contains("sp_getapplock", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(x => Equals(x.Value, "workshops:catalog"))) Arrived.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
