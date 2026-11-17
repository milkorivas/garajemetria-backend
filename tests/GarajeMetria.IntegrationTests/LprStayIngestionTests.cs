using System.Data.Common;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Lpr;
using GarajeMetria.Domain.Stays;
using GarajeMetria.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace GarajeMetria.IntegrationTests;

public sealed partial class LprIngestionTests
{
    [SqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DailyClosureAndNewExitSerializeAcrossHostsAndPreserveTheRealBoundary(bool closeFirst)
    {
        var actor = new Actor(); var clock = new Clock(); var gate = new ProjectionGate();
        using var first = Factory(actor, clock, services => services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(gate)));
        var device = await SeedAsync(first, actor);
        using var second = Factory(actor, clock, shared: first);
        var entryAt = clock.Now.AddMinutes(-5);
        var entry = Request(device, entryAt) with { PlateOriginal = "1234BCD", Direction = LprDirection.Entry };
        Assert.True((await SendAsync(first, entry)).Succeeded);
        await using var verify = database.CreateContext();
        var partition = await verify.VehicleStayPartitions.AsNoTracking().SingleAsync(x => x.WorkshopId == device.Device.WorkshopId);
        var date = DateOnly.FromDateTime(entryAt.UtcDateTime);
        var midnight = new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        clock.Now = midnight.AddMinutes(5);
        actor.Device = actor.Device! with { ExpiresAt = clock.Now.AddHours(1) };
        var exitAt = midnight.AddMinutes(1);
        var exit = Request(device, exitAt) with { PlateOriginal = "1234BCD", Direction = LprDirection.Exit };
        async Task Close(AuthenticationFactory factory)
        {
            using var scope = factory.Services.CreateScope();
            Assert.True((await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CloseStayDayCommand(partition.Id, date))).Succeeded);
        }
        async Task Ingest(AuthenticationFactory factory) => Assert.True((await SendAsync(factory, exit)).Succeeded);
        gate.Enabled = true;
        var leading = closeFirst ? Close(first) : Ingest(first);
        try
        {
            await gate.Barrier.Reached.WaitAsync(TimeSpan.FromSeconds(10));
            var following = closeFirst ? Ingest(second) : Close(second);
            gate.Barrier.Release();
            await Task.WhenAll(leading, following).WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { gate.Barrier.Release(); }
        verify.ChangeTracker.Clear();
        var stay = await verify.VehicleStays.AsNoTracking().SingleAsync(x => x.PartitionId == partition.Id && x.AbsorbedByStayId == null);
        Assert.Equal(entryAt, stay.EntryAt); Assert.Equal(exitAt, stay.ExitAt);
        Assert.Equal(StayBoundarySource.Explicit, stay.ExitSource);
        Assert.Equal(2, await verify.StayObservationAssignments.CountAsync(x => x.PartitionId == partition.Id));
        Assert.Equal(2, await verify.LprEvents.CountAsync(x => x.DeviceId == device.Device.Id));
        Assert.Equal(1, await verify.StayDayClosures.CountAsync(x => x.PartitionId == partition.Id && x.LocalDate == date));
    }

    [SqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidIngestionIsAtomicAndExplicitRetryReconcilesConfirmedRollbackOrUncertainCommit(bool loseAcknowledgement)
    {
        var actor = new Actor(); var clock = new Clock(); var fault = new CommitFault { AfterCommit = true };
        var stage = new AssignmentSaveFault();
        using var factory = Factory(actor, clock, services => services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(fault, stage)));
        var device = await SeedAsync(factory, actor);
        var request = Request(device, clock.Now) with { PlateOriginal = "1234BCD", Direction = LprDirection.Entry };
        fault.Enabled = loseAcknowledgement; stage.Enabled = !loseAcknowledgement;
        var error = await Assert.ThrowsAsync<PersistenceFailureException>(() => SendAsync(factory, request));
        Assert.Equal(loseAcknowledgement ? FailureCode.OperationOutcomeUnknown : FailureCode.InternalError, error.Code);
        await using (var verify = database.CreateContext())
        {
            Assert.Equal(loseAcknowledgement ? 1 : 0, await verify.LprEvents.CountAsync(x => x.EventId == request.EventId));
            Assert.Equal(loseAcknowledgement ? 1 : 0, await verify.LprObservationLocations.CountAsync(x => x.EventId == request.EventId));
            Assert.Equal(loseAcknowledgement ? 1 : 0, await verify.VehicleStayPartitions.CountAsync(x => x.WorkshopId == device.Device.WorkshopId));
        }
        fault.Enabled = false; stage.Enabled = false;
        var retried = await SendAsync(factory, request);
        Assert.True(retried.Succeeded); Assert.Equal(loseAcknowledgement, retried.Value!.Duplicate);
        await using var db = database.CreateContext();
        var partition = await db.VehicleStayPartitions.AsNoTracking().SingleAsync(x => x.WorkshopId == device.Device.WorkshopId);
        Assert.Equal(1, await db.VehicleStays.CountAsync(x => x.PartitionId == partition.Id));
        Assert.Equal(1, await db.StayProjectionRevisions.CountAsync(x => x.PartitionId == partition.Id));
        Assert.Equal(1, await db.LprObservationLocations.CountAsync(x => x.EventId == request.EventId));
    }

    [SqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocationUpdateAndIngestionUseOneDeviceOrderAndRetryKeepsTheOriginalBinding(bool locationFirst)
    {
        var actor = new Actor(); var clock = new Clock(); var gate = new LocationIngestionGate(locationFirst);
        using var first = Factory(actor, clock, services => services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(gate)));
        var device = await SeedAsync(first, actor); using var second = Factory(actor, clock, shared: first);
        var request = Request(device, clock.Now) with { PlateOriginal = "1234BCD", Direction = LprDirection.Entry };
        async Task Location(AuthenticationFactory factory)
        {
            using var scope = factory.Services.CreateScope();
            Assert.True((await scope.ServiceProvider.GetRequiredService<ISender>().Send(
                new UpdateDeviceLocationCommand(device.Device.Id, "Synthetic location", device.Device.Version))).Succeeded);
        }
        async Task Ingest(AuthenticationFactory factory) => Assert.True((await SendAsync(factory, request)).Succeeded);
        gate.Enabled = true;
        var leading = locationFirst ? Location(first) : Ingest(first);
        try
        {
            await gate.Barrier.Reached.WaitAsync(TimeSpan.FromSeconds(10));
            var following = locationFirst ? Ingest(second) : Location(second);
            gate.Barrier.Release();
            await Task.WhenAll(leading, following).WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { gate.Barrier.Release(); }
        await using var db = database.CreateContext();
        var link = await db.LprObservationLocations.AsNoTracking().SingleAsync(x => x.EventId == request.EventId);
        Assert.Equal(locationFirst, link.LocationId is not null);
        Assert.Equal(1, await db.LprDeviceLocations.CountAsync(x => x.DeviceId == device.Device.Id));
        var retry = await SendAsync(second, request); Assert.True(retry.Succeeded); Assert.True(retry.Value!.Duplicate);
        Assert.Equal(link.LocationId, (await db.LprObservationLocations.AsNoTracking().SingleAsync(x => x.EventId == request.EventId)).LocationId);
        Assert.Equal(1, await db.LprEvents.CountAsync(x => x.DeviceId == device.Device.Id));
        Assert.Equal(actor.Device!.AuthorizationVersion, (await db.LprDevices.AsNoTracking().SingleAsync(x => x.Id == device.Device.Id)).AuthorizationVersion);
    }

    private sealed class LocationIngestionGate(bool locationFirst) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public StayTestBarrier Barrier { get; } = new();
        private int paused;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var relevant = locationFirst ? data.Context!.ChangeTracker.Entries<LprDeviceLocationRow>().Any(x => x.State == EntityState.Added)
                : data.Context!.ChangeTracker.Entries<LprEventRow>().Any(x => x.State == EntityState.Added);
            if (Enabled && relevant && Interlocked.Exchange(ref paused, 1) == 0) await Barrier.PauseAsync(ct);
            return result;
        }
    }

    private sealed class AssignmentSaveFault : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<StayObservationAssignmentRow>().Any(x => x.State == EntityState.Added))
                throw new InvalidOperationException("Synthetic assignment failure before commit.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ProjectionGate : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public StayTestBarrier Barrier { get; } = new();
        private int paused;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (Enabled && command.CommandText.Contains("INSERT INTO [StayProjectionRevisions]", StringComparison.Ordinal) && Interlocked.Exchange(ref paused, 1) == 0)
                await Barrier.PauseAsync(ct);
            return result;
        }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && command.CommandText.Contains("INSERT INTO [StayProjectionRevisions]", StringComparison.Ordinal) && Interlocked.Exchange(ref paused, 1) == 0)
                await Barrier.PauseAsync(ct);
            return result;
        }
    }
}
