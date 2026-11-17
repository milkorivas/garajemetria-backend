using GarajeMetria.Infrastructure.Stays;

namespace GarajeMetria.IntegrationTests;

public sealed class StayConfigurationTests
{
    [Theory]
    [InlineData(3, 29, 23)]
    [InlineData(10, 25, 25)]
    [InlineData(11, 17, 24)]
    public void CanaryDaysUseBothLocalMidnights(int month, int day, int hours)
    {
        var calendar = new WorkshopCalendar();
        var date = new DateOnly(2026, month, day);
        var window = calendar.GetDay(date);
        Assert.Equal(TimeSpan.FromHours(hours), window.End - window.Start);
        Assert.Equal(date, calendar.GetLocalDate(window.Start));
        Assert.Equal(date, calendar.GetLocalDate(window.End.AddTicks(-1)));
        Assert.Equal(date.AddDays(1), calendar.GetLocalDate(window.End));
        Assert.Equal(TimeSpan.Zero, window.Start.Offset);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveBusinessDurationsAreRejected(int value)
    {
        Assert.Throws<InvalidOperationException>(() => (new StayOptions { EstimatedDurationMinutes = value }).Validate());
        Assert.Throws<InvalidOperationException>(() => (new StayOptions { RepeatWindowSeconds = value }).Validate());
        Assert.Throws<InvalidOperationException>(() => (new StayOptions { MaximumAssociationHours = value }).Validate());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(101, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1001)]
    public void OperationalPagesAndBatchesAreBounded(int batch, int page) =>
        Assert.Throws<InvalidOperationException>(() => (new StayOptions { ClosureBatchSize = batch, ObservationPageSize = page }).Validate());

    [Fact]
    public void FingerprintIsIndependentOfIdentityAndOperationalValues()
    {
        var original = new StayOptions();
        var operational = original with { ClosurePollSeconds = 5, ClosureBatchSize = 2, ObservationPageSize = 1 };
        original.Validate(); operational.Validate();
        Assert.Equal(StayOptions.Fingerprint(original.CreateSnapshot(Guid.NewGuid())), StayOptions.Fingerprint(operational.CreateSnapshot(Guid.NewGuid())));
        foreach (var changed in new[] { original with { EstimatedDurationMinutes = 45 }, original with { RepeatWindowSeconds = 90 }, original with { MaximumAssociationHours = 72 } })
            Assert.NotEqual(StayOptions.Fingerprint(original.CreateSnapshot(Guid.NewGuid())), StayOptions.Fingerprint(changed.CreateSnapshot(Guid.NewGuid())));
        Assert.Throws<InvalidOperationException>(() => (original with { MaximumAssociationHours = int.MaxValue }).Validate());
        Assert.Throws<InvalidOperationException>(() => (original with { ClosurePollSeconds = int.MaxValue }).Validate());
    }

    [Fact]
    public void UnsupportedCalendarExtremesAreRejected()
    {
        var calendar = new WorkshopCalendar();
        Assert.Throws<ArgumentOutOfRangeException>(() => calendar.GetDay(DateOnly.MinValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => calendar.GetDay(DateOnly.MaxValue));
    }
}
