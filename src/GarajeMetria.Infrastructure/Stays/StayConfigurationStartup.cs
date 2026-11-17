using GarajeMetria.Application.Stays;
using Microsoft.Extensions.Hosting;

namespace GarajeMetria.Infrastructure.Stays;

// Local validation only. Startup must never activate or create a shared business policy.
internal sealed class StayConfigurationStartup(StayOptions options, IWorkshopCalendar calendar, TimeProvider clock) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        var day = calendar.GetDay(calendar.GetLocalDate(clock.GetUtcNow()));
        try
        {
            foreach (var duration in new[] { options.EstimatedDuration, options.RepeatWindow, options.MaximumAssociationDuration })
            {
                _ = day.Start.Subtract(duration);
                _ = day.End.Add(duration);
            }
        }
        catch (ArgumentOutOfRangeException ex) { throw new InvalidOperationException("Stays durations exceed supported date arithmetic.", ex); }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
