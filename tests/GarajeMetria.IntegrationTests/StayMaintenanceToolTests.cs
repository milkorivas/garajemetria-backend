using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GarajeMetria.Domain.Lpr;
using GarajeMetria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace GarajeMetria.IntegrationTests;

public sealed class StayMaintenanceToolTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact(Timeout = 120000)]
    public async Task ToolProjectsResumesDiagnosesAndClosesOnlyTheIsolatedSyntheticDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GarajeMetria_StayMaintenanceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "private.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                ConnectionStrings = new { Authentication = database.ConnectionString },
                Stays = new { ObservationPageSize = 1 }
            }));
            var site = "cli-" + Guid.NewGuid().ToString("N"); var device = Guid.NewGuid();
            var captured = DateTimeOffset.UtcNow.AddDays(-2);
            await using (var db = database.CreateContext())
            {
                db.Workshops.Add(new() { Id = site, Code = site, Name = "Synthetic", IsActive = false });
                db.Set<OpenIddictEntityFrameworkCoreApplication>().Add(new() { Id = device.ToString("N"), ClientId = "lpr_" + device.ToString("N"), ClientType = OpenIddictConstants.ClientTypes.Confidential });
                db.LprDevices.Add(new(device, "Synthetic", site, true, captured.AddDays(-1))).Property<string>("OAuthApplicationId").CurrentValue = device.ToString("N");
                await db.SaveChangesAsync();
                for (var i = 0; i < 3; i++) db.LprEvents.Add(new()
                {
                    EventId = Guid.NewGuid(),
                    DeviceId = device,
                    WorkshopId = site,
                    CapturedAt = captured.AddMinutes(i * 5),
                    ReceivedAt = captured.AddMinutes(i * 5).AddSeconds(1),
                    PlateOriginal = "1234BCD",
                    PlateNormalized = "1234BCD",
                    PlateStatus = "valid",
                    PlateFormat = "synthetic-v0",
                    PlateRulesVersion = "saved-v0",
                    Confidence = 0m
                });
                await db.SaveChangesAsync();
            }
            Assert.Equal(1, (await RunAsync("project", path)).Code);
            Assert.Equal(0, (await RunAsync("diagnose", path)).Code);
            Assert.Equal(0, (await RunAsync("project", path, "--confirm-ingestion-stopped", "--max-units", "1")).Code);
            await using var verify = database.CreateContext();
            var stay = await verify.VehicleStays.AsNoTracking().SingleAsync(x => x.WorkshopId == site);
            Assert.Equal(3, await verify.StayObservationAssignments.CountAsync(x => x.PartitionId == stay.PartitionId));
            Assert.Equal(0, (await RunAsync("project", path, "--confirm-ingestion-stopped")).Code);
            var repeated = await RunAsync("project", path, "--confirm-ingestion-stopped");
            Assert.Equal(0, repeated.Code); Assert.Contains("Confirmed units: 0", repeated.Output);
            Assert.DoesNotContain(database.ConnectionString, repeated.Output);
            Assert.Equal(stay.Id, (await verify.VehicleStays.AsNoTracking().SingleAsync(x => x.WorkshopId == site)).Id);
            Assert.Equal(3, await verify.LprObservationLocations.CountAsync(x => x.DeviceId == device));
            var date = DateOnly.FromDateTime(captured.UtcDateTime);
            var end = new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            await verify.StayDayClosures.Where(x => x.PartitionId == stay.PartitionId).ExecuteDeleteAsync();
            await verify.VehicleStayPartitions.Where(x => x.Id == stay.PartitionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextClosureAt, end));
            Assert.Equal(0, (await RunAsync("close-due", path)).Code);
            Assert.Equal(1, await verify.StayDayClosures.CountAsync(x => x.PartitionId == stay.PartitionId));
            var version = await verify.StayConfiguration.Select(x => x.Version).SingleAsync();
            Assert.Equal(0, (await RunAsync("activate", path, "--expected-version", version.ToString("D"))).Code);
            Assert.Equal(version, await verify.StayConfiguration.Select(x => x.Version).SingleAsync());
            Assert.Equal(2, (await RunAsync("activate", path, "--expected-version", Guid.Empty.ToString("D"))).Code);
        }
        finally
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("GarajeMetria_StayMaintenanceTests_", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove an unexpected maintenance fixture directory.");
            Directory.Delete(full, recursive: true);
        }
    }

    private static async Task<(int Code, string Output)> RunAsync(string command, string config, params string[] extra)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GarajeMetria.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = typeof(StayMaintenanceToolTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var candidates = new[] { Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "GarajeMetria.StayMaintenance", configuration.ToLowerInvariant(), "GarajeMetria.StayMaintenance.dll")),
            Path.Combine(root.FullName, "tools", "GarajeMetria.StayMaintenance", "bin", configuration, "net10.0", "GarajeMetria.StayMaintenance.dll") };
        var assembly = candidates.FirstOrDefault(File.Exists);
        Assert.True(assembly is not null, "Build the solution including the maintenance tool before executing the CLI integration test.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = root.FullName
        };
        start.ArgumentList.Add(assembly!); start.ArgumentList.Add(command); start.ArgumentList.Add("--config"); start.ArgumentList.Add(config);
        foreach (var argument in extra) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output + await error);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
}
