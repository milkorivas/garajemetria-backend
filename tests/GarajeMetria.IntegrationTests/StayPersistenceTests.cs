using System.Data.Common;
using GarajeMetria.Application;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Lpr;
using GarajeMetria.Domain.Stays;
using GarajeMetria.Infrastructure;
using GarajeMetria.Infrastructure.Persistence;
using GarajeMetria.Infrastructure.Stays;
using GarajeMetria.Testing;
using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace GarajeMetria.IntegrationTests;

public sealed class StayPersistenceTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    private static readonly DateTimeOffset Monday = StayTestData.Monday;
    private static readonly DateTimeOffset Now = Monday.AddDays(4).AddHours(12);

    [SqlFact]
    public async Task InitialProjectionResumesFromDurableAssignmentsAndPreservesAllSourceFields()
    {
        var seed = await SeedAsync([(8, LprDirection.Entry), (8.5, LprDirection.Entry), (9.5, LprDirection.Exit)]);
        using var provider = Provider(pageSize: 1);
        await ProjectAsync(provider, seed.Events[0]);
        await using var db = database.CreateContext();
        var stay = await db.VehicleStays.AsNoTracking().SingleAsync(x => x.WorkshopId == seed.Site);
        Assert.Equal(Monday.AddHours(8), stay.EntryAt);
        Assert.Equal(Monday.AddHours(9.5), stay.ExitAt);
        Assert.Equal(3, await db.StayObservationAssignments.CountAsync(x => x.PartitionId == stay.PartitionId));
        Assert.Equal(3, await db.LprEvents.CountAsync(x => x.DeviceId == seed.Device));
        var immutable = await db.LprEvents.AsNoTracking().Where(x => x.DeviceId == seed.Device).OrderBy(x => x.EventId)
            .Select(x => new { x.EventId, x.PlateOriginal, x.PlateNormalized, x.PlateRulesVersion, x.CapturedAt, x.Confidence }).ToArrayAsync();
        var revisionCount = await db.StayProjectionRevisions.CountAsync(x => x.PartitionId == stay.PartitionId);
        foreach (var id in seed.Events) await ProjectAsync(provider, id);
        using (var scope = provider.CreateScope())
        {
            var pending = await scope.ServiceProvider.GetRequiredService<IStayMaintenanceStore>().ReadPendingAsync(1000, default);
            Assert.DoesNotContain(pending.EventIds, id => seed.Events.Contains(id));
            var audit = await scope.ServiceProvider.GetRequiredService<IObservationAuditQuery>().ReadAsync(
                new(seed.Device, seed.Site, Monday, Monday.AddDays(1), 10), null, default);
            Assert.Equal(3, audit.Items.Count);
            Assert.All(audit.Items, x => Assert.Equal(ObservationLocationProvenance.Unknown, x.Location.Provenance));
            Assert.Empty((await scope.ServiceProvider.GetRequiredService<IObservationAuditQuery>().ReadAsync(
                new(seed.Device, seed.Site.ToUpperInvariant(), Monday, Monday.AddDays(1), 10), null, default)).Items);
        }
        db.ChangeTracker.Clear();
        Assert.Equal(stay.Id, (await db.VehicleStays.AsNoTracking().SingleAsync(x => x.WorkshopId == seed.Site)).Id);
        Assert.Equal(revisionCount, await db.StayProjectionRevisions.CountAsync(x => x.PartitionId == stay.PartitionId));
        Assert.Equal(immutable, await db.LprEvents.AsNoTracking().Where(x => x.DeviceId == seed.Device).OrderBy(x => x.EventId)
            .Select(x => new { x.EventId, x.PlateOriginal, x.PlateNormalized, x.PlateRulesVersion, x.CapturedAt, x.Confidence }).ToArrayAsync());
    }

    [SqlFact]
    public async Task KeysetPagesUseCanonicalUuidBytesInBothDirectionsAndIncludeLowConfidence()
    {
        var seed = await SeedAsync([]);
        var ids = new[] { Guid.Parse("00000001-0000-4000-8000-000000000000"), Guid.Parse("01000000-0000-4000-8000-000000000000"),
            Guid.Parse("00000000-0001-4000-8000-000000000000") };
        await using (var db = database.CreateContext())
        {
            foreach (var id in ids) db.LprEvents.Add(Event(seed, id, Monday.AddHours(8), null));
            await db.SaveChangesAsync();
        }
        using var provider = Provider(pageSize: 1);
        await ProjectAsync(provider, ids[0]);
        await using var verify = database.CreateContext();
        var row = await verify.VehicleStayPartitions.AsNoTracking().SingleAsync(x => x.WorkshopId == seed.Site);
        using var scope = provider.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IStayObservationQuery>();
        foreach (var direction in new[] { StayReadDirection.Forward, StayReadDirection.Backward })
        {
            var actual = new List<Guid>();
            StayObservationCursor? cursor = null;
            do
            {
                var page = await query.ReadAsync(new(StayRepository.Partition(row), Monday, Monday.AddDays(1), 1, direction), cursor, default);
                actual.AddRange(page.Items.Select(x => x.Observation.EventId)); cursor = page.NextCursor;
                Assert.All(page.Items, x => Assert.Equal(0m, x.Observation.Confidence));
            } while (cursor is not null);
            var expected = ids.OrderBy(id => Convert.ToHexString(id.ToByteArray(bigEndian: true)), StringComparer.Ordinal);
            Assert.Equal(direction == StayReadDirection.Forward ? expected : expected.Reverse(), actual);
        }
    }

    [SqlFact]
    public async Task TwoInstancesProjectAndCloseOnePartitionWithoutDuplicatingHistory()
    {
        var seed = await SeedAsync([(8, LprDirection.Entry), (9, LprDirection.Exit)]);
        using var first = Provider(pageSize: 1); using var second = Provider(pageSize: 1);
        await Task.WhenAll(ProjectAsync(first, seed.Events[0]), ProjectAsync(second, seed.Events[1]));
        await using var db = database.CreateContext();
        var partition = await db.VehicleStayPartitions.AsNoTracking().SingleAsync(x => x.WorkshopId == seed.Site);
        var before = await db.StayProjectionRevisions.CountAsync(x => x.PartitionId == partition.Id);
        var closures = await Task.WhenAll(CloseAsync(first, partition.Id), CloseAsync(second, partition.Id));
        Assert.All(closures, result => Assert.True(result.Succeeded));
        Assert.Equal(1, await db.VehicleStays.CountAsync(x => x.PartitionId == partition.Id));
        Assert.Equal(2, await db.StayObservationAssignments.CountAsync(x => x.PartitionId == partition.Id));
        Assert.Equal(1, await db.StayDayClosures.CountAsync(x => x.PartitionId == partition.Id));
        Assert.Equal(before, await db.StayProjectionRevisions.CountAsync(x => x.PartitionId == partition.Id));
    }

    [SqlFact]
    public async Task FailedFusionRollsBackEveryPhaseAndRetryRetainsFirstCreatedIdentityAndPolicy()
    {
        var seed = await SeedAsync([(9, LprDirection.Entry), (8, LprDirection.Entry)]);
        using var normal = Provider();
        var oldPolicy = await ActiveAsync(normal);
        var alternate = new StayPolicySnapshot(Guid.NewGuid(), TimeSpan.FromMinutes(45), TimeSpan.FromSeconds(90), TimeSpan.FromHours(72), "Atlantic/Canary", 1);
        await StorePolicyAsync(alternate);
        var winner = await SeedProjectionAsync(normal, seed, [seed.Events[0]], seed.Events[0], oldPolicy, Now, 1);
        var absorbed = await SeedProjectionAsync(normal, seed, [seed.Events[1]], seed.Events[1], alternate, Now.AddMinutes(1), 2);
        var fault = new LineageFault { Enabled = true };
        using var failing = Provider(fault: fault);
        var failure = await Assert.ThrowsAsync<PersistenceFailureException>(() => CloseAsync(failing, winner.Partition));
        Assert.Equal(FailureCode.InternalError, failure.Code);
        await using var db = database.CreateContext();
        Assert.Equal(2, await db.VehicleStays.CountAsync(x => x.PartitionId == winner.Partition && x.AbsorbedByStayId == null));
        Assert.Equal(2, await db.StayProjectionRevisions.CountAsync(x => x.PartitionId == winner.Partition));
        Assert.Empty(await db.StayLineage.Where(x => x.PartitionId == winner.Partition).ToArrayAsync());
        Assert.True((await CloseAsync(normal, winner.Partition)).Succeeded);
        db.ChangeTracker.Clear();
        var active = await db.VehicleStays.AsNoTracking().SingleAsync(x => x.PartitionId == winner.Partition && x.AbsorbedByStayId == null);
        Assert.Equal(winner.Id, active.Id); Assert.Equal(seed.Events[0], active.OriginEventId); Assert.Equal(oldPolicy.Id, active.PolicyId);
        Assert.Equal(Monday.AddHours(8), active.EntryAt);
        Assert.Equal(winner.Id, (await db.VehicleStays.AsNoTracking().SingleAsync(x => x.Id == absorbed.Id)).AbsorbedByStayId);
        Assert.Single(await db.StayLineage.Where(x => x.PartitionId == winner.Partition).ToArrayAsync());
        Assert.Equal(2, await db.LprEvents.CountAsync(x => x.DeviceId == seed.Device));
    }

    [SqlFact]
    public async Task SplitPreservesLaterAnchorAndHistoricalPolicyAndRetryDoesNotCreateMoreParts()
    {
        var seed = await SeedAsync([(8, null), (9, null), (16, null), (17, null)]);
        using var original = Provider();
        var policy = await ActiveAsync(original);
        var old = await SeedProjectionAsync(original, seed, seed.Events, seed.Events[2], policy, Now, 1);
        using var changed = Provider(estimatedMinutes: 45);
        await ActivateAsync(changed, 45, Now.AddMinutes(1));
        try
        {
            var additions = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            await using (var db = database.CreateContext())
            {
                db.LprEvents.AddRange(Event(seed, additions[0], Monday.AddHours(8), LprDirection.Entry), Event(seed, additions[1], Monday.AddHours(9), LprDirection.Exit),
                    Event(seed, additions[2], Monday.AddHours(16), LprDirection.Entry), Event(seed, additions[3], Monday.AddHours(17), LprDirection.Exit));
                await db.SaveChangesAsync();
            }
            await ProjectAsync(changed, additions[0]);
            await using var verify = database.CreateContext();
            var parts = await verify.VehicleStays.AsNoTracking().Where(x => x.PartitionId == old.Partition && x.AbsorbedByStayId == null).ToArrayAsync();
            Assert.Equal(2, parts.Length); Assert.All(parts, p => Assert.Equal(policy.Id, p.PolicyId));
            Assert.Equal(Monday.AddHours(16), parts.Single(p => p.Id == old.Id).EntryAt);
            Assert.Equal(seed.Events[2], parts.Single(p => p.Id == old.Id).OriginEventId);
            var ids = parts.Select(p => p.Id).Order().ToArray();
            await ProjectAsync(changed, additions[0]); await CloseAsync(changed, old.Partition);
            var persistedIds = await verify.VehicleStays.AsNoTracking().Where(x => x.PartitionId == old.Partition && x.AbsorbedByStayId == null).Select(x => x.Id).ToArrayAsync();
            Assert.Equal(ids, persistedIds.Order());
            Assert.Single(await verify.StayLineage.Where(x => x.PartitionId == old.Partition && x.Kind == StayLineageKind.Split).ToArrayAsync());
            Assert.Equal(8, await verify.LprEvents.CountAsync(x => x.DeviceId == seed.Device));
        }
        finally { await ActivateAsync(original, 30, Now.AddMinutes(2)); }
    }

    [SqlFact]
    public async Task ContextBudgetExhaustionRollsBackInputProgressAndLocationInsteadOfPersistingAPartialStay()
    {
        var seed = await SeedAsync([(8, null), (9, null), (10, null)]);
        using var provider = Provider(observationLimit: 2);
        var failure = await Assert.ThrowsAsync<PersistenceFailureException>(() => ProjectAsync(provider, seed.Events[0]));
        Assert.Equal(FailureCode.ServiceUnavailable, failure.Code);
        await using var db = database.CreateContext();
        Assert.Empty(await db.VehicleStayPartitions.Where(x => x.WorkshopId == seed.Site).ToArrayAsync());
        Assert.Empty(await db.LprObservationLocations.Where(x => x.DeviceId == seed.Device).ToArrayAsync());
        Assert.Equal(3, await db.LprEvents.CountAsync(x => x.DeviceId == seed.Device));
    }

    [SqlFact]
    public async Task ConstraintsRejectInvalidBoundariesForeignLineageAndForeignObservationLocation()
    {
        var first = await SeedAsync([(8, LprDirection.Entry), (9, LprDirection.Exit)]);
        var second = await SeedAsync([(8, LprDirection.Entry)]);
        using var provider = Provider(); await ProjectAsync(provider, first.Events[0]); await ProjectAsync(provider, second.Events[0]);
        await using var db = database.CreateContext();
        var a = await db.VehicleStays.AsNoTracking().SingleAsync(x => x.WorkshopId == first.Site);
        var b = await db.VehicleStays.AsNoTracking().SingleAsync(x => x.WorkshopId == second.Site);
        var foreign = Guid.NewGuid();
        Assert.Equal(547, Number(await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [VehicleStays] SET [ExitAt] = {Monday} WHERE [Id] = {a.Id}"))));
        Assert.Equal(547, Number(await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [VehicleStays] SET [EntrySource] = 'Estimated' WHERE [Id] = {a.Id}"))));
        Assert.Equal(547, Number(await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [VehicleStays] SET [EntryEventId] = {foreign} WHERE [Id] = {a.Id}"))));
        db.StayLineage.Add(new() { PartitionId = a.PartitionId, Revision = a.Revision, SourceStayId = a.Id, TargetStayId = b.Id, Kind = StayLineageKind.Fusion, RecordedAt = Now });
        Assert.Equal(547, Number(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()))); db.ChangeTracker.Clear();
        var policy = await db.StayPolicies.SingleAsync(x => x.Id == a.PolicyId); policy.RepeatWindowTicks++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        var locationId = Guid.NewGuid();
        db.LprDeviceLocations.Add(new() { Id = locationId, DeviceId = second.Device, WorkshopId = second.Site, Description = "Synthetic other access", RecordedAt = Now, CreationOrder = 1 });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        // The first event already has an Unknown link; changing it to another device's location must fail.
        Assert.Equal(547, Number(await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [LprObservationLocations] SET [LocationId] = {locationId}, [Provenance] = 'AtCapture' WHERE [EventId] = {first.Events[0]}"))));
        Assert.Equal(Monday.AddHours(8), (await db.VehicleStays.AsNoTracking().SingleAsync(x => x.Id == a.Id)).EntryAt);
    }

    [SqlFact]
    public async Task InvalidReadingsRemainAuditableByOriginalTextAndDeviceWithoutAStayPartition()
    {
        var seed = await SeedAsync([]); var id = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            db.LprEvents.Add(new()
            {
                EventId = id,
                DeviceId = seed.Device,
                WorkshopId = seed.Site,
                CapturedAt = Monday.AddHours(8),
                ReceivedAt = Monday.AddHours(8).AddSeconds(1),
                PlateOriginal = "synthetic-invalid",
                PlateNormalized = "SYNTHETICINVALID",
                PlateStatus = "invalid",
                PlateRulesVersion = "saved-v0",
                Confidence = 0m
            });
            await db.SaveChangesAsync();
        }
        using var provider = Provider(); await ProjectAsync(provider, id);
        using var scope = provider.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IObservationAuditQuery>();
        var page = await query.ReadAsync(new(seed.Device, seed.Site, Monday, Monday.AddDays(1), 1, "synthetic-invalid"), null, default);
        var observation = Assert.Single(page.Items);
        Assert.Equal(id, observation.EventId); Assert.Equal("invalid", observation.PlateStatus);
        Assert.Equal(ObservationLocationProvenance.Unknown, observation.Location.Provenance);
        Assert.Empty((await query.ReadAsync(new(Guid.NewGuid(), seed.Site, Monday, Monday.AddDays(1), 1, "synthetic-invalid"), null, default)).Items);
        await using var verify = database.CreateContext();
        Assert.False(await verify.VehicleStayPartitions.AnyAsync(x => x.WorkshopId == seed.Site));
        Assert.False(await verify.StayObservationAssignments.AnyAsync(x => x.EventId == id));
    }

    private static int Number(Exception error)
    { for (Exception? current = error; current is not null; current = current.InnerException) if (current is SqlException sql) return sql.Number; throw new InvalidOperationException("Expected an isolated SQL constraint failure."); }

    private ServiceProvider Provider(int pageSize = 2, int estimatedMinutes = 30, int observationLimit = 20000, LineageFault? fault = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Authentication"] = database.ConnectionString,
            ["Stays:ObservationPageSize"] = pageSize.ToString(),
            ["Stays:EstimatedDurationMinutes"] = estimatedMinutes.ToString(),
            ["Stays:ContextObservationLimit"] = observationLimit.ToString()
        }).Build();
        var services = new ServiceCollection(); services.AddApplication(); services.AddStayMaintenance(config);
        services.AddSingleton<ILprClock>(new StayTestClock(new StayTestTimeProvider(Now.AddHours(1))));
        if (fault is not null) services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(fault));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
    private static async Task ProjectAsync(ServiceProvider provider, Guid id)
    {
        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ProjectStayObservationCommand(id));
        Assert.True(result.Succeeded, result.Error?.Code.ToString());
    }
    private static async Task<Outcome<CloseStayDayResult>> CloseAsync(ServiceProvider provider, Guid partition)
    { using var scope = provider.CreateScope(); return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CloseStayDayCommand(partition, DateOnly.FromDateTime(Monday.UtcDateTime))); }
    private static async Task<StayPolicySnapshot> ActiveAsync(ServiceProvider provider)
    { using var scope = provider.CreateScope(); return (await scope.ServiceProvider.GetRequiredService<IStayPolicyRepository>().GetActiveAsync(default)).Policy; }
    private async Task ActivateAsync(ServiceProvider provider, int minutes, DateTimeOffset at)
    {
        await using var db = database.CreateContext(); var version = await db.StayConfiguration.Select(x => x.Version).SingleAsync();
        using var scope = provider.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IStayPolicyRepository>().ActivateAsync(
            (new StayOptions { EstimatedDurationMinutes = minutes }).CreateSnapshot(Guid.NewGuid()), version, at, default));
    }
    private async Task StorePolicyAsync(StayPolicySnapshot policy)
    {
        await using var db = database.CreateContext();
        db.StayPolicies.Add(new()
        {
            Id = policy.Id,
            Fingerprint = StayOptions.Fingerprint(policy),
            EstimatedDurationTicks = policy.EstimatedDuration.Ticks,
            RepeatWindowTicks = policy.RepeatWindow.Ticks,
            MaximumAssociationTicks = policy.MaximumAssociationDuration.Ticks,
            TimeZoneId = policy.TimeZoneId,
            AlgorithmVersion = policy.AlgorithmVersion
        });
        await db.SaveChangesAsync();
    }
    private async Task<(Guid Id, Guid Partition)> SeedProjectionAsync(ServiceProvider provider, Seed seed, Guid[] events, Guid anchor,
        StayPolicySnapshot policy, DateTimeOffset created, long creationOrder)
    {
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IStayMaintenanceStore>();
        var repository = scope.ServiceProvider.GetRequiredService<IStayRepository>();
        var result = await store.ExecuteAsync(async ct =>
        {
            var partition = await repository.GetOrCreatePartitionAsync(seed.Site, "1234BCD", ct);
            var readings = new List<StayObservation>();
            foreach (var id in events) readings.Add((await store.FindObservationAsync(id, ct))!);
            var days = readings.Select(x => DateOnly.FromDateTime(x.CapturedAt.UtcDateTime)).Distinct()
                .Select(date => new StayDayWindow(date, new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))).ToArray();
            var candidate = Assert.Single(StayCalculator.Calculate(readings, policy, days, created));
            // Explicit fixture identity permits an anchor later than the first capture.
            var idValue = Guid.NewGuid();
            var initial = StayRecalculator.Recalculate([], [candidate], created, (o, p) => StayCalculator.Calculate(o, p, days, created), _ => new(idValue, creationOrder));
            if (anchor != candidate.Observations[0].EventId)
            {
                // Construct the historical projection with its original anchor through the public domain value.
                var anchored = new StayProjection(idValue, anchor, created, creationOrder, candidate);
                initial = new StayRecalculation([anchored], [anchored], [], [], []);
            }
            await repository.ApplyAsync(new(partition.Id, partition.InputRevision, partition.ProjectionRevision, StayProjectionCause.InitialProjection,
                created, null, Monday.AddDays(1), initial), ct);
            return Outcome<(Guid, Guid)>.Success((idValue, partition.Id));
        }, default);
        Assert.True(result.Succeeded);
        return result.Value;
    }
    private async Task<Seed> SeedAsync((double Hour, LprDirection? Direction)[] readings)
    {
        var site = "stay-" + Guid.NewGuid().ToString("N"); var device = Guid.NewGuid();
        await using var db = database.CreateContext();
        db.Workshops.Add(new() { Id = site, Code = site, Name = "Synthetic", IsActive = true });
        db.Set<OpenIddictEntityFrameworkCoreApplication>().Add(new() { Id = device.ToString("N"), ClientId = "lpr_" + device.ToString("N"), ClientType = OpenIddictConstants.ClientTypes.Confidential });
        db.LprDevices.Add(new(device, "Synthetic", site, true, Monday)).Property<string>("OAuthApplicationId").CurrentValue = device.ToString("N");
        await db.SaveChangesAsync();
        var seed = new Seed(site, device, readings.Select(_ => Guid.NewGuid()).ToArray());
        for (var i = 0; i < readings.Length; i++) db.LprEvents.Add(Event(seed, seed.Events[i], Monday.AddHours(readings[i].Hour), readings[i].Direction));
        await db.SaveChangesAsync();
        return seed;
    }
    private static LprEventRow Event(Seed seed, Guid id, DateTimeOffset at, LprDirection? direction) => new()
    {
        EventId = id,
        DeviceId = seed.Device,
        WorkshopId = seed.Site,
        CapturedAt = at,
        ReceivedAt = at.AddSeconds(1),
        PlateOriginal = "1234BCD",
        PlateNormalized = "1234BCD",
        PlateStatus = "valid",
        PlateFormat = "synthetic-saved-format",
        PlateRulesVersion = "synthetic-historical-v0",
        Confidence = 0m,
        Direction = direction
    };
    private sealed record Seed(string Site, Guid Device, Guid[] Events);
    private sealed class LineageFault : SaveChangesInterceptor
    {
        public bool Enabled { get; init; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<StayLineageRow>().Any(x => x.State == EntityState.Added))
                throw new InvalidOperationException("Synthetic failure between assignment and lineage.");
            return ValueTask.FromResult(result);
        }
    }
}
