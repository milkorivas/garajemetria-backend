using System.Data.Common;
using System.Net;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Infrastructure.Persistence;
using GarajeMetria.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GarajeMetria.IntegrationTests;

// One-shot checkpoint for race tests. Always release in finally and bound waits at the caller.
public sealed class StayTestBarrier
{
    private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Reached => reached.Task;
    public void Signal() => reached.TrySetResult();
    public void Release() => released.TrySetResult();
    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        Signal();
        await released.Task.WaitAsync(cancellationToken);
    }
}

public sealed class StayTestClock(StayTestTimeProvider time) : ILprClock
{
    public Task<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(time.GetUtcNow());
    }
}

public sealed class StayPreparationTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task TwoHostsUseIsolatedMigratedSqlAndShareControlledTimeWithoutDirectoryCalls()
    {
        using var environment = new LprTestEnvironment(StayTestData.Monday);
        var clock = new StayTestTimeProvider(StayTestData.Monday);
        using var first = new AuthenticationFactory(database, configureServices: Configure, lprEnvironment: environment);
        using var firstClient = first.Client();
        using var second = new AuthenticationFactory(database, shared: first, configureServices: Configure, lprEnvironment: environment);
        using var secondClient = second.Client();
        using var firstResponse = await firstClient.GetAsync("/api/auth/antiforgery");
        using var secondResponse = await secondClient.GetAsync("/api/auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        using var firstScope = first.Services.CreateScope();
        using var secondScope = second.Services.CreateScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<AuthenticationDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<AuthenticationDbContext>();
        Assert.NotSame(firstDb, secondDb);
        Assert.Equal(database.DatabaseName, firstDb.Database.GetDbConnection().Database);
        Assert.Equal(database.DatabaseName, secondDb.Database.GetDbConnection().Database);
        Assert.Matches(@"^GarajeMetria_AuthTests_[0-9a-f]{32}$", database.DatabaseName);
        Assert.True(await firstDb.Database.CanConnectAsync());
        Assert.True(await secondDb.Database.CanConnectAsync());
        Assert.Empty(await firstDb.Database.GetPendingMigrationsAsync());
        Assert.Empty(await secondDb.Database.GetPendingMigrationsAsync());
        clock.SetUtcNow(StayTestData.Monday.AddDays(1));
        foreach (var scope in new[] { firstScope, secondScope })
        {
            Assert.Equal(clock.GetUtcNow(), await scope.ServiceProvider.GetRequiredService<ILprClock>().GetUtcNowAsync(default));
            Assert.Same(clock, scope.ServiceProvider.GetRequiredService<TimeProvider>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<ILprUnitOfWork>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<ILprEventRepository>());
        }
        Assert.Equal(0, first.DirectoryAdapter.Calls);
        Assert.Equal(0, second.DirectoryAdapter.Calls);

        void Configure(IServiceCollection services)
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<ILprClock>();
            services.AddScoped<ILprClock>(_ => new StayTestClock(clock));
        }
    }

    [SqlFact]
    public async Task IndependentHostWaitsForTheRealCatalogTransactionUntilExplicitRelease()
    {
        using var environment = new LprTestEnvironment();
        var probe = new CatalogProbe();
        using var first = new AuthenticationFactory(database, lprEnvironment: environment);
        using var firstClient = first.Client();
        using var second = new AuthenticationFactory(database, shared: first, lprEnvironment: environment,
            configureServices: services => services.AddDbContext<AuthenticationDbContext>(o => o.AddInterceptors(probe)));
        using var secondClient = second.Client();
        using var scope = second.Services.CreateScope();
        await using var holder = database.CreateContext();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await SqlTransactions.LockAsync(holder, "workshops:catalog", default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        probe.Enabled = true;
        var entered = false;
        var waiting = scope.ServiceProvider.GetRequiredService<ILprUnitOfWork>()
            .ExecuteAsync(new(null, Guid.NewGuid()), _ =>
            {
                entered = true;
                return Task.FromResult(true);
            }, timeout.Token);
        try
        {
            await probe.Barrier.Reached.WaitAsync(timeout.Token);
            Assert.False(entered);
            Assert.False(waiting.IsCompleted);
        }
        finally { await transaction.CommitAsync(); }
        Assert.True(await waiting.WaitAsync(timeout.Token));
        Assert.True(entered);
        Assert.Equal(0, first.DirectoryAdapter.Calls);
        Assert.Equal(0, second.DirectoryAdapter.Calls);
    }

    private sealed class CatalogProbe : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public StayTestBarrier Barrier { get; } = new();
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("sp_getapplock", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, "workshops:catalog")))
                Barrier.Signal();
            return ValueTask.FromResult(result);
        }
    }
}
