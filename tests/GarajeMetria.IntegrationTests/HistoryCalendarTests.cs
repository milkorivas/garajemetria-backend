using GarajeMetria.Application.History;
using GarajeMetria.Infrastructure.History;
using GarajeMetria.Infrastructure.Stays;

namespace GarajeMetria.IntegrationTests;

public sealed class HistoryCalendarTests
{
    [Theory]
    [InlineData(3, 29, 23)]
    [InlineData(10, 25, 25)]
    public void WholeCanaryDaysRespectDaylightSavingTransitions(int month, int day, int hours)
    {
        var service = new HistoryTime(new WorkshopCalendar(), new HistoryCalendar());
        var date = new DateOnly(2026, month, day);
        var window = service.Resolve(new SearchLprEventsQuery(new() { FromDate = date, ThroughDate = date }), DateTimeOffset.UtcNow);
        Assert.NotNull(window);
        Assert.Equal(hours, (window.End - window.Start).TotalHours);
    }

    [Fact]
    public void RepeatedLocalTimeIsUnambiguousWhenOffsetsAreSupplied()
    {
        var first = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.FromHours(1));
        var second = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero);
        var calendar = new HistoryCalendar();
        Assert.Equal(calendar.GetLocalTime(first), calendar.GetLocalTime(second));
        Assert.Equal(3600m, HistoryTime.Duration(first, second, second));
        var window = new HistoryTime(new WorkshopCalendar(), calendar).Resolve(new SearchLprEventsQuery(new() { From = first, To = second }), second);
        Assert.NotNull(window);
        Assert.Equal(TimeSpan.FromHours(1), window.End - window.Start);
    }
}
