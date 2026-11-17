using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Stays;

namespace GarajeMetria.Infrastructure.Stays;

public sealed class WorkshopCalendar : IWorkshopCalendar
{
    private readonly TimeZoneInfo zone;

    public WorkshopCalendar()
    {
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(StayOptions.TimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new InvalidOperationException("The required workshop time zone is unavailable.", ex); }
    }

    public DateOnly GetLocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    public StayDayWindow GetDay(DateOnly localDate)
    {
        if (localDate <= DateOnly.MinValue || localDate >= DateOnly.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(localDate));
        return new(localDate, Midnight(localDate), Midnight(localDate.AddDays(1)));
    }

    private DateTimeOffset Midnight(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            throw new InvalidOperationException("The workshop day boundary cannot be resolved unambiguously.");
        return new(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }
}
