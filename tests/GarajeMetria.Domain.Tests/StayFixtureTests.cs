using GarajeMetria.Testing;

namespace GarajeMetria.Domain.Tests;

public sealed class StayFixtureTests
{
    [Fact]
    public void CanaryCalendarHasCorrectUtcBoundsOnBothClockChanges()
    {
        var calendar = new StayTestCalendar();
        var spring = calendar.Bounds(new(2026, 3, 29));
        var autumn = calendar.Bounds(new(2026, 10, 25));
        Assert.Equal(TimeSpan.FromHours(23), spring.End - spring.Start);
        Assert.Equal(TimeSpan.FromHours(25), autumn.End - autumn.Start);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 23, 0, 0, TimeSpan.Zero), spring.End);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 23, 0, 0, TimeSpan.Zero), autumn.Start);
        Assert.Equal(new DateOnly(2026, 3, 30), calendar.DateAt(spring.End));
        Assert.Equal(new DateOnly(2026, 10, 25), calendar.DateAt(autumn.End.AddTicks(-1)));
    }

    [Fact]
    public void ControlledTimeCanCrossMidnightWithoutWaitingForTheSystemClock()
    {
        var clock = new StayTestTimeProvider(StayTestData.Monday.AddDays(1).AddTicks(-1));
        var calendar = new StayTestCalendar();
        Assert.Equal(new DateOnly(2026, 11, 16), calendar.DateAt(clock.GetUtcNow()));
        clock.SetUtcNow(StayTestData.Monday.AddDays(1));
        Assert.Equal(new DateOnly(2026, 11, 17), calendar.DateAt(clock.GetUtcNow()));
        Assert.Equal(StayTestData.Monday.AddDays(1), clock.GetUtcNow());
    }
}
