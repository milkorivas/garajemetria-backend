using GarajeMetria.Domain.Lpr;

namespace GarajeMetria.Domain.Tests;

public sealed class LprEventTests
{
    private static readonly Guid EventId = Guid.Parse("8d663ca0-cb10-4b41-8dfb-c850d61637dc");
    private static readonly Guid DeviceId = Guid.Parse("43cc450c-1431-4e9b-bb18-43fe078e927c");
    private static readonly DateTimeOffset Capture = DateTimeOffset.UnixEpoch;
    private static LprEvent Create(decimal confidence = 0.00000001m, LprDirection? direction = null,
        string plate = "0000-bbb", DateTimeOffset? received = null) =>
        new(EventId, DeviceId, "synthetic-site", Capture, received ?? Capture.AddMinutes(1), plate, confidence, direction);

    [Fact]
    public void ObservationPreservesOriginalDiagnosticExactConfidenceAndSeparateUtcInstants()
    {
        var result = new LprEvent(EventId, DeviceId, "synthetic-site", Capture.ToOffset(TimeSpan.FromHours(1)),
            Capture.AddMinutes(1).ToOffset(TimeSpan.FromHours(-2)), "0000-bbb", 0.00000001m, null);
        Assert.Equal(EventId, result.EventId);
        Assert.Equal(DeviceId, result.DeviceId);
        Assert.Equal("synthetic-site", result.WorkshopId);
        Assert.Equal(Capture, result.CapturedAt);
        Assert.Equal(Capture.AddMinutes(1), result.ReceivedAt);
        Assert.Equal(TimeSpan.Zero, result.CapturedAt.Offset);
        Assert.Equal(TimeSpan.Zero, result.ReceivedAt.Offset);
        Assert.Equal("0000-bbb", result.PlateOriginal);
        Assert.Equal("0000BBB", result.PlateNormalized);
        Assert.Equal("valid", result.PlateStatus);
        Assert.Equal("spanish_national", result.PlateFormat);
        Assert.Equal("es-v1", result.PlateRulesVersion);
        Assert.Equal(0.00000001m, result.Confidence);
        Assert.Null(result.Direction);
        Assert.Equal(nameof(LprEvent), result.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ConfidenceBoundariesAndBothDirectionsAreRetained(int direction)
    {
        var result = Create(direction, (LprDirection)direction);
        Assert.Equal((decimal)direction, result.Confidence);
        Assert.Equal((LprDirection)direction, result.Direction);
    }

    [Fact]
    public void UnrecognizedFormatIsRetainedAndCanBeComparedWithoutReclassification()
    {
        var result = Create(plate: "synthetic-invalid");
        Assert.Equal("invalid", result.PlateStatus);
        Assert.Null(result.PlateFormat);
        Assert.Equal("synthetic-invalid", result.PlateOriginal);
        Assert.True(result.Matches(EventId, DeviceId, "synthetic-site", Capture, "synthetic-invalid", result.Confidence, null));
    }

    [Fact]
    public void RetryComparisonIgnoresReceiptAndUsesInstantDecimalAndNullableDirectionValues()
    {
        var saved = Create(confidence: 0.10m);
        var late = Create(confidence: 0.100000000m, received: Capture.AddYears(1));
        Assert.True(saved.Matches(late.EventId, late.DeviceId, late.WorkshopId,
            late.CapturedAt.ToOffset(TimeSpan.FromHours(3)), late.PlateOriginal, late.Confidence, late.Direction));
        Assert.Equal(Capture.AddMinutes(1), saved.ReceivedAt);
    }

    [Fact]
    public void EveryIdentityOrPayloadChangeIsAConflictEvenWhenNormalizedPlateMatches()
    {
        var saved = Create();
        Assert.False(saved.Matches(Guid.NewGuid(), DeviceId, "synthetic-site", Capture, saved.PlateOriginal, saved.Confidence, null));
        Assert.False(saved.Matches(EventId, Guid.NewGuid(), "synthetic-site", Capture, saved.PlateOriginal, saved.Confidence, null));
        Assert.False(saved.Matches(EventId, DeviceId, "SYNTHETIC-SITE", Capture, saved.PlateOriginal, saved.Confidence, null));
        Assert.False(saved.Matches(EventId, DeviceId, "synthetic-site", Capture.AddTicks(1), saved.PlateOriginal, saved.Confidence, null));
        Assert.False(saved.Matches(EventId, DeviceId, "synthetic-site", Capture, "0000BBB", saved.Confidence, null));
        Assert.False(saved.Matches(EventId, DeviceId, "synthetic-site", Capture, saved.PlateOriginal, 0.2m, null));
        Assert.False(saved.Matches(EventId, DeviceId, "synthetic-site", Capture, saved.PlateOriginal, saved.Confidence, LprDirection.Entry));
        Assert.Equal(saved.PlateNormalized, Create(plate: "0000BBB").PlateNormalized);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void ThirtyMinuteWindowIncludesExactBoundariesAndRejectsOneTickBeyond(int edge)
    {
        var window = new LprCaptureWindow(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        Assert.True(window.Contains(Capture.AddMinutes(edge * 30), Capture));
        Assert.False(window.Contains(Capture.AddMinutes(-30).AddTicks(-1), Capture));
        Assert.False(window.Contains(Capture.AddMinutes(30).AddTicks(1), Capture));
    }

    [Fact]
    public void WindowTolerancesAreIndependentAndDoNotOverflowAtDateTimeLimits()
    {
        var window = new LprCaptureWindow(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2));
        Assert.True(window.Contains(Capture.AddMinutes(-5), Capture));
        Assert.False(window.Contains(Capture.AddMinutes(-5).AddTicks(-1), Capture));
        Assert.True(window.Contains(Capture.AddMinutes(2), Capture));
        Assert.False(window.Contains(Capture.AddMinutes(2).AddTicks(1), Capture));
        Assert.True(window.Contains(Capture.ToOffset(TimeSpan.FromHours(1)), Capture));
        Assert.True(window.Contains(DateTimeOffset.MinValue, DateTimeOffset.MinValue));
        Assert.True(window.Contains(DateTimeOffset.MaxValue, DateTimeOffset.MaxValue));
        Assert.False(window.Contains(DateTimeOffset.MaxValue, DateTimeOffset.MinValue));
        var zero = new LprCaptureWindow(TimeSpan.Zero, TimeSpan.Zero);
        Assert.True(zero.Contains(Capture, Capture));
        Assert.False(zero.Contains(Capture.AddTicks(1), Capture));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LprCaptureWindow(TimeSpan.FromTicks(-1), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LprCaptureWindow(TimeSpan.Zero, TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void StructurallyInvalidEventsAreRejectedWithoutRoundingConfidence()
    {
        foreach (var confidence in new[] { -0.1m, 1.1m, 0.000000001m })
            Assert.Throws<ArgumentException>(() => Create(confidence));
        Assert.Equal(0.1234567800m, Create(0.1234567800m).Confidence);
        Assert.Throws<ArgumentException>(() => Create(direction: (LprDirection)99));
        Assert.Throws<ArgumentException>(() => Create(plate: " -- "));
        Assert.Throws<ArgumentException>(() => new LprEvent(Guid.Empty, DeviceId, "site", Capture, Capture, "0000BBB", 1m, null));
        Assert.Throws<ArgumentException>(() => new LprEvent(Guid.AllBitsSet, DeviceId, "site", Capture, Capture, "0000BBB", 1m, null));
        Assert.Throws<ArgumentException>(() => new LprEvent(EventId, Guid.Empty, "site", Capture, Capture, "0000BBB", 1m, null));
        Assert.Throws<ArgumentException>(() => new LprEvent(EventId, DeviceId, new string('x', 257), Capture, Capture, "0000BBB", 1m, null));
    }

    [Theory]
    [InlineData("8d663ca0-cb10-1b41-8dfb-c850d61637dc", true)]
    [InlineData("8d663ca0-cb10-2b41-8dfb-c850d61637dc", true)]
    [InlineData("8d663ca0-cb10-3b41-8dfb-c850d61637dc", true)]
    [InlineData("8d663ca0-cb10-4b41-8dfb-c850d61637dc", true)]
    [InlineData("8d663ca0-cb10-5b41-8dfb-c850d61637dc", true)]
    [InlineData("8d663ca0-cb10-6b41-8dfb-c850d61637dc", true)]
    [InlineData("8d663ca0-cb10-7b41-8dfb-c850d61637dc", true)]
    [InlineData("8d663ca0-cb10-8b41-8dfb-c850d61637dc", true)]
    [InlineData("8d663ca0-cb10-9b41-8dfb-c850d61637dc", false)]
    [InlineData("8d663ca0-cb10-4b41-0dfb-c850d61637dc", false)]
    [InlineData("8d663ca0-cb10-4b41-cdfb-c850d61637dc", false)]
    public void EventUuidSupportsStandardVersionsRatherThanOnlyRandomV4(string text, bool valid) =>
        Assert.Equal(valid, LprEventRules.ValidEventId(Guid.Parse(text)));
}
