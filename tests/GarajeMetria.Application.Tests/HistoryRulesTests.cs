using GarajeMetria.Application.History;
using GarajeMetria.Application.Stays;

namespace GarajeMetria.Application.Tests;

public sealed class HistoryRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 50, true)]
    [InlineData(1, 200, true)]
    [InlineData(0, 50, false)]
    [InlineData(1, 201, false)]
    [InlineData(int.MaxValue, 200, true)]
    public async Task PageLimitsAreValidatedWithoutOverflow(int number, int size, bool valid) =>
        Assert.Equal(valid, (await new HistoryValidator().ValidateAsync(new SearchLprEventsQuery(new() { PageNumber = number, PageSize = size }))).IsValid);

    [Fact]
    public async Task InvalidCombinationsAreRejectedBeforePersistence()
    {
        HistoryFilter[] invalid = [new() { ConfidenceMode = "range" }, new() { ConfidenceMode = "missing", ConfidenceMin = 0 },
            new() { ConfidenceMode = "range", ConfidenceMin = .8m, ConfidenceMax = .2m },
            new() { ConfidenceMode = "range", ConfidenceMin = .123456789m }, new() { Plate = "123", OriginalText = "123" },
            new() { From = Now }, new() { From = Now, To = Now }, new() { Order = "SQL" },
            new() { DeviceIds = [Guid.Empty] }, new() { WorkshopIds = Enumerable.Repeat("site", 101).ToArray() },
            new() { Plate = "---" }, new() { FromDate = DateOnly.FromDateTime(Now.DateTime), ThroughDate = DateOnly.MaxValue }];
        foreach (var filter in invalid) Assert.False((await new HistoryValidator().ValidateAsync(new SearchLprEventsQuery(filter))).IsValid);
        Assert.False((await new HistoryValidator().ValidateAsync(new GetVehicleTimelineQuery(new() { Plate = "123", PlateMatch = "contains" }))).IsValid);
        Assert.False((await new HistoryValidator().ValidateAsync(new ListPresentVehiclesQuery(new() { From = Now, To = Now.AddHours(1) }))).IsValid);
    }

    [Theory]
    [InlineData("0012-bcd", "0012BCD")]
    [InlineData(" 0 0 1 2 B c D ", "0012BCD")]
    [InlineData("12O", "12O")]
    public void PlateSearchDoesNotCorrectCharacters(string input, string expected) => Assert.Equal(expected, HistoryValidator.Normalize(input));

    [Fact]
    public void OverlapIncludesEarlierVisitsAndPointsWithoutInventingMissingBoundaries()
    {
        var window = new HistoryWindow(Now, Now.AddHours(1));
        Assert.True(HistoryTime.Overlaps(Now.AddDays(-1), Now.AddDays(1), window, Now.AddMinutes(30)));
        Assert.False(HistoryTime.Overlaps(Now.AddHours(-1), Now, window, Now));
        Assert.False(HistoryTime.Overlaps(window.End, window.End, window, Now));
        Assert.True(HistoryTime.Overlaps(Now, Now, window, Now));
        Assert.True(HistoryTime.Overlaps(null, Now, window, Now));
        Assert.True(HistoryTime.Overlaps(Now.AddDays(-1), null, window, Now.AddMinutes(1)));
        Assert.True(HistoryTime.Overlaps(Now.AddMinutes(10), null, window, Now));
    }

    [Fact]
    public void MaximumYearUsesLocalCalendarAndIncludesLeapYear()
    {
        var calendar = new Calendar();
        var service = new HistoryTime(calendar, calendar);
        var year = service.Resolve(new SearchLprEventsQuery(new() { FromDate = new(2024, 1, 1), ThroughDate = new(2024, 12, 31) }), Now);
        Assert.NotNull(year);
        Assert.Equal(366, (year.End - year.Start).TotalDays);
        Assert.Null(service.Resolve(new SearchLprEventsQuery(new() { FromDate = new(2024, 1, 1), ThroughDate = new(2025, 1, 1) }), Now));
        var leapDay = service.Resolve(new SearchLprEventsQuery(new() { From = new(2024, 2, 29, 0, 0, 0, TimeSpan.Zero), To = new(2025, 2, 28, 0, 0, 0, TimeSpan.Zero) }), Now);
        Assert.NotNull(leapDay);
    }
    private sealed class Calendar : IWorkshopCalendar, IHistoryCalendar
    {
        public DateTime GetLocalTime(DateTimeOffset instant) => instant.UtcDateTime;
        public DateOnly GetLocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);
        public GarajeMetria.Domain.Stays.StayDayWindow GetDay(DateOnly date) => new(date,
            new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
    }
}
