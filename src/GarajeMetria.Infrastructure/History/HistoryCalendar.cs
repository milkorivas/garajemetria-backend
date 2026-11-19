using GarajeMetria.Application.History;

namespace GarajeMetria.Infrastructure.History;

internal sealed class HistoryCalendar : IHistoryCalendar
{
    private readonly TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById("Atlantic/Canary");
    public DateTime GetLocalTime(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, zone).DateTime;
}
