using GarajeMetria.Application.Diagnostics;
using GarajeMetria.Infrastructure.Authentication;
using GarajeMetria.Infrastructure.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.IntegrationTests;

public sealed class HealthLoggingTests
{
    [Fact]
    public async Task ScopedDecoratorsShareTransitionsAndRecordRecoveryWithoutRepeatingWarnings()
    {
        var logs = new RecordingLogProvider();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        var tracker = new HealthStatusTracker(factory.CreateLogger<HealthStatusTracker>());
        var diagnostics = new StubDiagnostics();
        await new HealthDiagnosticsLoggingDecorator(diagnostics, tracker).CheckActiveDirectoryAsync(default);
        await new HealthDiagnosticsLoggingDecorator(diagnostics, tracker).CheckSqlAsync(default);
        Assert.Empty(logs.Entries);

        diagnostics.Status = ModuleStatus.Error;
        await new HealthDiagnosticsLoggingDecorator(diagnostics, tracker).CheckActiveDirectoryAsync(default);
        await new HealthDiagnosticsLoggingDecorator(diagnostics, tracker).CheckActiveDirectoryAsync(default);
        Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Single(logs.Entries, e => e.Level == LogLevel.Debug);
        Assert.Equal("activeDirectory", logs.Entries.First().Properties["Module"]);

        diagnostics.Status = ModuleStatus.Ok;
        await new HealthDiagnosticsLoggingDecorator(diagnostics, tracker).CheckActiveDirectoryAsync(default);
        Assert.Contains("recovered", Assert.Single(logs.Entries, e => e.Level == LogLevel.Information).Message);
        diagnostics.Status = ModuleStatus.Error;
        await new HealthDiagnosticsLoggingDecorator(diagnostics, tracker).CheckActiveDirectoryAsync(default);
        Assert.Equal(2, logs.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task InitialFailureAndMissingConfigurationAreVisibleIndependentlyForBothModules()
    {
        var logs = new RecordingLogProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var tracker = new HealthStatusTracker(factory.CreateLogger<HealthStatusTracker>());
        var diagnostics = new StubDiagnostics { Status = ModuleStatus.Error };
        var decorator = new HealthDiagnosticsLoggingDecorator(diagnostics, tracker);
        await decorator.CheckActiveDirectoryAsync(default);
        diagnostics.Status = ModuleStatus.NotConfigured;
        await decorator.CheckSqlAsync(default);
        Assert.Equal(2, logs.Entries.Count(e => e.Level == LogLevel.Warning));
        Assert.Contains(logs.Entries, e => Equals(e.Properties["Status"], ModuleStatus.NotConfigured));
    }

    [Fact]
    public async Task SqlProbeLogsEveryConvertedExceptionWithoutTheConnectionString()
    {
        var logs = new RecordingLogProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        // Parsing fails before any connection is attempted; no substitute database is used.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Authentication"] = "private-invalid-key=private-password"
        }).Build();
        var diagnostics = new HealthDiagnostics(configuration, new DirectoryOptions(), factory.CreateLogger<HealthDiagnostics>());
        Assert.Equal(ModuleStatus.Error, await diagnostics.CheckSqlAsync(default));
        Assert.Equal(ModuleStatus.Error, await diagnostics.CheckSqlAsync(default));
        Assert.Equal(2, logs.Entries.Count);
        Assert.All(logs.Entries, e =>
        {
            Assert.Equal("Health.Sql.Probe", e.Properties["Operation"]);
            Assert.Equal("ArgumentException", e.Properties["ExceptionType"]);
            Assert.Null(e.Exception);
            Assert.DoesNotContain("private-", e.Message);
        });
    }

    private sealed class StubDiagnostics : IHealthDiagnostics
    {
        public ModuleStatus Status { get; set; } = ModuleStatus.Ok;
        public Task<ModuleStatus> CheckSqlAsync(CancellationToken cancellationToken) => Task.FromResult(Status);
        public Task<ModuleStatus> CheckActiveDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult(Status);
    }
}
