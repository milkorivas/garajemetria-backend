using System.Text.Json;
using GarajeMetria.Application;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Stays;
using GarajeMetria.Infrastructure;
using GarajeMetria.Infrastructure.Stays;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (args.Length == 0 || args[0] is not ("diagnose" or "activate" or "project" or "close-due"))
        throw new ArgumentException("Expected diagnose, activate, project or close-due.");
    string? path = Environment.GetEnvironmentVariable("GARAJEMETRIA_PRIVATE_CONFIG");
    Guid? expected = null;
    var stopped = false;
    var maxUnits = int.MaxValue;
    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--config" when i + 1 < args.Length: path = args[++i]; break;
            case "--expected-version" when i + 1 < args.Length && Guid.TryParse(args[++i], out var version): expected = version; break;
            case "--confirm-ingestion-stopped": stopped = true; break;
            case "--max-units" when i + 1 < args.Length && int.TryParse(args[++i], out var limit) && limit > 0: maxUnits = limit; break;
            default: throw new ArgumentException("Invalid maintenance argument.");
        }
    }
    if (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new ArgumentException("An external private configuration file is required.");
    if (args[0] == "project" && !stopped) throw new ArgumentException("Initial projection requires stopped ingestion acknowledgement.");
    if (args[0] == "activate" && expected is null) throw new ArgumentException("Activation requires an explicit expected version.");
    var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false, reloadOnChange: false).Build();
    var services = new ServiceCollection();
    services.AddApplication();
    services.AddStayMaintenance(configuration);
    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    foreach (var startup in provider.GetServices<IHostedService>()) await startup.StartAsync(cancellation.Token);
    if (args[0] == "diagnose")
    {
        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IStayDiagnostics>().ReadAsync(cancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(result));
        return result.SchemaAvailable && result.PolicyMatches ? 0 : 2;
    }
    if (args[0] == "activate")
    {
        using var scope = provider.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<StayOptions>();
        var now = await scope.ServiceProvider.GetRequiredService<ILprClock>().GetUtcNowAsync(cancellation.Token);
        var result = await scope.ServiceProvider.GetRequiredService<IStayPolicyRepository>().ActivateAsync(options.CreateSnapshot(Guid.NewGuid()), expected!.Value, now, cancellation.Token);
        Console.WriteLine(result ? "Policy activation confirmed." : "Configuration version conflict; review shared state.");
        return result ? 0 : 2;
    }
    var completed = 0;
    while (completed < maxUnits)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        using var discovery = provider.CreateScope();
        if (args[0] == "project")
        {
            var page = await discovery.ServiceProvider.GetRequiredService<IStayMaintenanceStore>().ReadPendingAsync(Math.Min(100, maxUnits - completed), cancellation.Token);
            if (page.EventIds.Count == 0) break;
            foreach (var id in page.EventIds)
            {
                using var scope = provider.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ProjectStayObservationCommand(id), cancellation.Token);
                if (!result.Succeeded) throw new PersistenceFailureException(result.Error!.Code);
                completed++;
            }
        }
        else
        {
            var now = await discovery.ServiceProvider.GetRequiredService<ILprClock>().GetUtcNowAsync(cancellation.Token);
            var page = await discovery.ServiceProvider.GetRequiredService<IStayClosureRepository>().ReadDueAsync(now, Math.Min(100, maxUnits - completed), null, cancellation.Token);
            if (page.Items.Count == 0) break;
            foreach (var work in page.Items)
            {
                using var scope = provider.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CloseStayDayCommand(work.PartitionId, work.LocalDate), cancellation.Token);
                if (!result.Succeeded) throw new PersistenceFailureException(result.Error!.Code);
                completed++;
            }
        }
        Console.WriteLine($"Confirmed units: {completed}");
    }
    Console.WriteLine($"Maintenance finished. Confirmed units: {completed}");
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Maintenance cancelled; resume from durable progress."); return 3; }
catch (PersistenceFailureException ex) { Console.Error.WriteLine($"Maintenance stopped: {ex.Code}. Review state before retrying."); return 2; }
catch (Exception ex) { Console.Error.WriteLine($"Maintenance stopped: {ex.GetType().Name}. Review configuration and safe diagnostics."); return 1; }
