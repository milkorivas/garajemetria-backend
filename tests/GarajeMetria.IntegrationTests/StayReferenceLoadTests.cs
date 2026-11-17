using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using GarajeMetria.Application;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Stays;
using GarajeMetria.Infrastructure;
using GarajeMetria.Infrastructure.Persistence;
using GarajeMetria.Infrastructure.Stays;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GarajeMetria.IntegrationTests;

public sealed partial class LprHttpTests
{
    [SqlFact(Timeout = 180000)]
    public async Task ReferenceLoadPreservesAtomicityWithTwoCamerasHistoryPolicyCutoverAndWorkerRecovery()
    {
        var catalogHolds = new ConcurrentBag<double>(); var catalogWaits = new ConcurrentBag<double>();
        var calculations = new ConcurrentBag<double>(); var labels = new ConcurrentBag<(string Key, string Value)>();
        var measuring = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) => { if (instrument.Meter.Name == "GarajeMetria.Stays") current.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (Volatile.Read(ref measuring) == 0) return;
            if (instrument.Name == "garajemetria.persistence.catalog.hold.duration") catalogHolds.Add(value);
            if (instrument.Name == "garajemetria.persistence.catalog.wait.duration") catalogWaits.Add(value);
            if (instrument.Name == "garajemetria.stays.calculation.duration") calculations.Add(value);
            foreach (var tag in tags) labels.Add((tag.Key, tag.Value?.ToString() ?? ""));
        });
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        { if (Volatile.Read(ref measuring) != 0) foreach (var tag in tags) labels.Add((tag.Key, tag.Value?.ToString() ?? "")); });
        listener.SetMeasurementEventCallback<int>((_, _, tags, _) =>
        { if (Volatile.Read(ref measuring) != 0) foreach (var tag in tags) labels.Add((tag.Key, tag.Value?.ToString() ?? "")); });
        listener.Start();
        var probe = new ClosureCommitted();
        using var first = Factory(configure: registration => registration.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(probe)));
        var setup = await SetupAsync(first); using var admin = Client(first, setup.Access);
        var a = await CreateAsync(admin, setup.Site); var b = await CreateAsync(admin, setup.Site);
        using var oauth = first.Client();
        var tokenA = (await ReadAsync(await TokenAsync(oauth, a.ClientId, a.Secret))).GetProperty("access_token").GetString()!;
        var tokenB = (await ReadAsync(await TokenAsync(oauth, b.ClientId, b.Secret))).GetProperty("access_token").GetString()!;
        var historyStart = DateTimeOffset.UtcNow.AddDays(-3);
        var historyIds = Enumerable.Range(0, 1200).Select(_ => Guid.NewGuid()).ToArray();
        await using (var seed = database.CreateContext())
        {
            seed.LprEvents.AddRange(historyIds.Select((id, i) => new LprEventRow
            {
                EventId = id,
                DeviceId = a.Id,
                WorkshopId = setup.Site,
                CapturedAt = historyStart.AddSeconds(i * 10),
                ReceivedAt = historyStart.AddSeconds(i * 10 + 1),
                PlateOriginal = "9876BCD",
                PlateNormalized = "9876BCD",
                PlateStatus = "valid",
                PlateFormat = "synthetic-reference",
                PlateRulesVersion = "saved-reference-v0",
                Confidence = 0m
            }));
            await seed.SaveChangesAsync();
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Authentication"] = database.ConnectionString, ["Stays:ObservationPageSize"] = "64" }).Build();
        var services = new ServiceCollection(); services.AddApplication(); services.AddStayMaintenance(configuration);
        using (var maintenance = services.BuildServiceProvider())
        using (var scope = maintenance.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ProjectStayObservationCommand(historyIds[0]))).Succeeded);
        Guid historyPartition;
        DateOnly historyDate = new WorkshopCalendar().GetLocalDate(historyStart);
        await using (var db = database.CreateContext())
        {
            historyPartition = await db.VehicleStayPartitions.Where(x => x.WorkshopId == setup.Site && x.PlateNormalized == "9876BCD").Select(x => x.Id).SingleAsync();
            probe.Watch(historyPartition);
            Assert.Equal(1200, await db.StayObservationAssignments.CountAsync(x => x.PartitionId == historyPartition));
            await db.StayDayClosures.Where(x => x.PartitionId == historyPartition).ExecuteDeleteAsync();
            var dueAt = new WorkshopCalendar().GetDay(historyDate).End;
            await db.VehicleStayPartitions.Where(x => x.Id == historyPartition).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextClosureAt, dueAt));
        }
        using var second = Factory(shared: first, configure: registration =>
        {
            registration.RemoveAll<StayOptions>(); registration.AddSingleton(new StayOptions { EstimatedDurationMinutes = 45 });
            registration.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(probe));
        });
        using var firstA = Client(first, tokenA); using var firstB = Client(first, tokenB);
        using var secondA = Client(second, tokenA); using var secondB = Client(second, tokenB);
        // Start the second host while the original policy is still active; it must wait for explicit activation.
        _ = second.Services;
        var latencies = new List<double>(); var duplicates = 0;
        var allocatedBefore = GC.GetTotalAllocatedBytes(); var managedBefore = GC.GetTotalMemory(false);
        using var process = Process.GetCurrentProcess(); var workingBefore = process.WorkingSet64;
        var timer = Stopwatch.StartNew();
        Volatile.Write(ref measuring, 1);
        try
        {
            for (var i = 0; i < 40; i++)
            {
                if (i == 20)
                {
                    using var scope = second.Services.CreateScope(); await using var db = database.CreateContext();
                    var version = await db.StayConfiguration.Select(x => x.Version).SingleAsync();
                    var now = await scope.ServiceProvider.GetRequiredService<ILprClock>().GetUtcNowAsync(default);
                    Assert.True(await scope.ServiceProvider.GetRequiredService<IStayPolicyRepository>().ActivateAsync(
                        (new StayOptions { EstimatedDurationMinutes = 45 }).CreateSnapshot(Guid.NewGuid()), version, now, default));
                }
                var client = i < 20 ? (i % 2 == 0 ? firstA : firstB) : (i % 2 == 0 ? secondA : secondB);
                var device = i % 2 == 0 ? a.Id : b.Id;
                var direction = i % 8 == 0 ? "entry" : i % 8 == 4 ? "exit" : null;
                var body = StayBody(Guid.NewGuid(), device, setup.Site, DateTimeOffset.UtcNow.AddSeconds(-(i % 3)), direction,
                    i >= 30 ? "5678BCF" : "1234BCD");
                var started = Stopwatch.GetTimestamp();
                Assert.Equal(HttpStatusCode.Created, (await PostEventAsync(client, body)).StatusCode);
                latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (i % 10 == 0) { Assert.Equal(HttpStatusCode.OK, (await PostEventAsync(client, body)).StatusCode); duplicates++; }
                await Task.Delay(TimeSpan.FromMilliseconds(1500)); // Explicit reference pacing, not a concurrency barrier.
            }
            await probe.Committed.Task.WaitAsync(TimeSpan.FromSeconds(35));
            timer.Stop(); Volatile.Write(ref measuring, 0); process.Refresh();
            await using var verify = database.CreateContext();
            Assert.Equal(1240, await verify.LprEvents.CountAsync(x => x.WorkshopId == setup.Site));
            Assert.Equal(1240, await verify.StayObservationAssignments.CountAsync(x => x.WorkshopId == setup.Site));
            Assert.True(await verify.StayDayClosures.AnyAsync(x => x.PartitionId == historyPartition && x.LocalDate == historyDate));
            Assert.Null((await verify.VehicleStayPartitions.AsNoTracking().SingleAsync(x => x.Id == historyPartition)).NextClosureAt);
            Assert.NotEmpty(catalogHolds); Assert.NotEmpty(calculations);
            Assert.All(labels, tag =>
            {
                Assert.Contains(tag.Key, new[] { "cause", "outcome", "change" });
                Assert.Contains(tag.Value, new[] { "ingestion", "daily_closure", "initial_projection", "created", "updated", "absorbed", "completed", "failed" });
            });
            var report = new
            {
                outcome = "passed",
                newEvents = 40,
                duplicateRequests = duplicates,
                historyEvents = 1200,
                cameras = 2,
                requestedPacingMilliseconds = 1500,
                measuredSeconds = timer.Elapsed.TotalSeconds,
                measuredNewEventsPerMinute = 40 / timer.Elapsed.TotalMinutes,
                httpMilliseconds = new { median = Percentile(latencies, .5), p95 = Percentile(latencies, .95), maximum = latencies.Max() },
                catalogHoldMilliseconds = new { p95 = Percentile(catalogHolds, .95) * 1000, maximum = catalogHolds.Max() * 1000 },
                catalogWaitMilliseconds = new { p95 = Percentile(catalogWaits, .95) * 1000 },
                calculationMilliseconds = new { p95 = Percentile(calculations, .95) * 1000 },
                managedBytesBefore = managedBefore,
                managedBytesAfter = GC.GetTotalMemory(false),
                allocatedBytes = GC.GetTotalAllocatedBytes() - allocatedBefore,
                workingSetBytesBefore = workingBefore,
                workingSetBytesAfter = process.WorkingSet64,
                peakWorkingSetBytes = process.PeakWorkingSet64,
                workerRecovered = true,
                policyCutover = true,
                runtime = Environment.Version.ToString(),
                processors = Environment.ProcessorCount,
                scope = "Isolated synthetic SQL Server and WebApplicationFactory; reference only, no production SLA or physical backup claim."
            };
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "GarajeMetria.slnx"))) root = root.Parent;
            Assert.NotNull(root);
            var directory = Path.Combine(root.FullName, "artifacts", "agent-checks"); Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "rf004-reference-load.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            Volatile.Write(ref measuring, 0);
            using var scope = first.Services.CreateScope(); await using var db = database.CreateContext();
            var version = await db.StayConfiguration.Select(x => x.Version).SingleAsync();
            var now = await scope.ServiceProvider.GetRequiredService<ILprClock>().GetUtcNowAsync(default);
            Assert.True(await scope.ServiceProvider.GetRequiredService<IStayPolicyRepository>().ActivateAsync(
                (new StayOptions()).CreateSnapshot(Guid.NewGuid()), version, now, default));
        }
    }

    private static double Percentile(IEnumerable<double> source, double percentile)
    { var values = source.Order().ToArray(); return values.Length == 0 ? 0 : values[Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1)]; }
    private sealed class ClosureCommitted : DbTransactionInterceptor
    {
        private readonly ConcurrentDictionary<Guid, byte> watched = new();
        public void Watch(Guid partitionId) => watched.TryAdd(partitionId, 0);
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
        {
            if (data.Context!.ChangeTracker.Entries<StayDayClosureRow>().Any(x => watched.ContainsKey(x.Entity.PartitionId))) Committed.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
