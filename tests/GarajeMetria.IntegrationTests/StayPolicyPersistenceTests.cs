using GarajeMetria.Application;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Stays;
using GarajeMetria.Infrastructure;
using GarajeMetria.Infrastructure.Persistence;
using GarajeMetria.Infrastructure.Stays;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GarajeMetria.IntegrationTests;

public sealed class StayPolicyPersistenceTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task ActivationIsExplicitVersionedAndHistoricalSnapshotsAreNotReplaced()
    {
        await using var verify = database.CreateContext();
        await verify.StayConfiguration.ExecuteDeleteAsync();
        await verify.StayPolicies.ExecuteDeleteAsync();
        using var first = Provider(30); using var second = Provider(45);
        using var firstScope = first.CreateScope(); using var secondScope = second.CreateScope();
        var oldNode = firstScope.ServiceProvider.GetRequiredService<IStayPolicyRepository>();
        var newNode = secondScope.ServiceProvider.GetRequiredService<IStayPolicyRepository>();
        Assert.Equal(FailureCode.ServiceUnavailable, (await Assert.ThrowsAsync<PersistenceFailureException>(() => oldNode.GetActiveAsync(default))).Code);
        var original = (new StayOptions()).CreateSnapshot(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GarajeMetria.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var script = await File.ReadAllTextAsync(Path.Combine(root.FullName, "docs", "configuration", "sql", "activate-stay-policy.sql"));
        await verify.Database.ExecuteSqlRawAsync(script);
        Assert.Equal(original.EstimatedDuration, (await oldNode.GetActiveAsync(default)).Policy.EstimatedDuration);
        await verify.StayConfiguration.ExecuteDeleteAsync(); await verify.StayPolicies.ExecuteDeleteAsync();
        Assert.True(await oldNode.ActivateAsync(original, Guid.Empty, now, default));
        var active = await oldNode.GetActiveAsync(default);
        Assert.True(await oldNode.ActivateAsync((new StayOptions()).CreateSnapshot(Guid.NewGuid()), active.ConfigurationVersion, now.AddMinutes(1), default));
        var repeated = await oldNode.GetActiveAsync(default);
        Assert.Equal(active, repeated);
        Assert.Single(await verify.StayPolicies.ToArrayAsync());
        Assert.False(await oldNode.ActivateAsync(original, Guid.NewGuid(), now.AddMinutes(2), default));
        Assert.Equal(FailureCode.ServiceUnavailable, (await Assert.ThrowsAsync<PersistenceFailureException>(() => newNode.GetActiveAsync(default))).Code);
        Assert.Equal(original, await newNode.FindAsync(original.Id, default));
        var changed = (new StayOptions { EstimatedDurationMinutes = 45 }).CreateSnapshot(Guid.NewGuid());
        Assert.True(await newNode.ActivateAsync(changed, active.ConfigurationVersion, now.AddMinutes(2), default));
        var newActive = await newNode.GetActiveAsync(default);
        Assert.NotEqual(active.ConfigurationVersion, newActive.ConfigurationVersion);
        Assert.Equal(FailureCode.ServiceUnavailable, (await Assert.ThrowsAsync<PersistenceFailureException>(() => oldNode.GetActiveAsync(default))).Code);
        Assert.Equal(original, await newNode.FindAsync(original.Id, default));
        var conflictingId = new StayPolicySnapshot(original.Id, changed.EstimatedDuration, changed.RepeatWindow,
            changed.MaximumAssociationDuration, changed.TimeZoneId, changed.AlgorithmVersion);
        await Assert.ThrowsAsync<PersistenceFailureException>(() => newNode.ActivateAsync(conflictingId, newActive.ConfigurationVersion, now.AddMinutes(3), default));
        verify.ChangeTracker.Clear();
        Assert.Equal(2, await verify.StayPolicies.CountAsync());
        Assert.Equal(original, await oldNode.FindAsync(original.Id, default));
        Assert.Equal(newActive, await newNode.GetActiveAsync(default));
        var oldStatus = await firstScope.ServiceProvider.GetRequiredService<IStayDiagnostics>().ReadAsync(default);
        var newStatus = await secondScope.ServiceProvider.GetRequiredService<IStayDiagnostics>().ReadAsync(default);
        Assert.True(oldStatus.SchemaAvailable); Assert.False(oldStatus.PolicyMatches);
        Assert.True(newStatus.SchemaAvailable); Assert.True(newStatus.PolicyMatches);
    }

    private ServiceProvider Provider(int minutes)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Authentication"] = database.ConnectionString, ["Stays:EstimatedDurationMinutes"] = minutes.ToString() }).Build();
        var services = new ServiceCollection(); services.AddApplication(); services.AddStayMaintenance(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
