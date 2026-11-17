using GarajeMetria.Domain.Lpr;
using GarajeMetria.Domain.Stays;
using GarajeMetria.Testing;

namespace GarajeMetria.Domain.Tests;

public sealed class StayValueTests
{
    private static readonly Guid EventId = StayTestData.Reading(1, StayTestData.Monday, null).Id;
    private static readonly Guid DeviceId = Guid.Parse("20000000-0000-4000-8000-000000000001");

    [Theory]
    [InlineData(0, 60, 48)]
    [InlineData(-1, 60, 48)]
    [InlineData(30, 0, 48)]
    [InlineData(30, -1, 48)]
    [InlineData(30, 60, 0)]
    [InlineData(30, 60, -1)]
    public void PolicyRejectsNonPositiveBusinessWindows(int minutes, int seconds, int hours) =>
        Assert.Throws<ArgumentException>(() => new StayPolicySnapshot(EventId, TimeSpan.FromMinutes(minutes),
            TimeSpan.FromSeconds(seconds), TimeSpan.FromHours(hours), "Atlantic/Canary", 1));

    [Theory]
    [InlineData("UTC", 1)]
    [InlineData("", 1)]
    [InlineData(null, 1)]
    [InlineData("Atlantic/Canary", 0)]
    [InlineData("Atlantic/Canary", -1)]
    public void PolicyRequiresTheAgreedZoneAndAValidAlgorithmVersion(string? zone, int version) =>
        Assert.Throws<ArgumentException>(() => new StayPolicySnapshot(EventId, TimeSpan.FromMinutes(30),
            TimeSpan.FromSeconds(60), TimeSpan.FromHours(48), zone!, version));

    [Fact]
    public void PolicyRequiresIdentityAndAcceptsConfiguredValuesWithoutDefaultingToThirtyMinutes()
    {
        Assert.Throws<ArgumentException>(() => new StayPolicySnapshot(Guid.Empty, TimeSpan.FromMinutes(30),
            TimeSpan.FromSeconds(60), TimeSpan.FromHours(48), "Atlantic/Canary", 1));
        var configured = StayTestData.ChangedPolicy;
        var result = new StayPolicySnapshot(EventId, TimeSpan.FromMinutes(configured.EstimatedMinutes),
            TimeSpan.FromSeconds(configured.RepeatSeconds), TimeSpan.FromHours(configured.MaximumHours), "Atlantic/Canary", 2);
        Assert.Equal(TimeSpan.FromMinutes(45), result.EstimatedDuration);
        Assert.Equal(TimeSpan.FromSeconds(90), result.RepeatWindow);
        Assert.Equal(TimeSpan.FromHours(72), result.MaximumAssociationDuration);
        Assert.Equal(2, result.AlgorithmVersion);
    }

    [Fact]
    public void ProjectionPreservesStoredClassificationInsteadOfInvokingTodaysPlateClassifier()
    {
        var captured = StayTestData.Monday.ToOffset(TimeSpan.FromHours(2));
        var received = StayTestData.Monday.AddMinutes(1).ToOffset(TimeSpan.FromHours(-1));
        var observation = new StayObservation(EventId, DeviceId, "synthetic-site", "SAVEDUNKNOWNFORMAT",
            captured, received, true, "historical-rule", 0.00000001m, null);
        Assert.True(observation.IsPlateValid);
        Assert.Equal("SAVEDUNKNOWNFORMAT", observation.PlateNormalized);
        Assert.Equal("historical-rule", observation.PlateRulesVersion);
        Assert.Equal(0.00000001m, observation.Confidence);
        Assert.Null(observation.Direction);
        Assert.Equal(StayTestData.Monday, observation.CapturedAt);
        Assert.Equal(StayTestData.Monday.AddMinutes(1), observation.ReceivedAt);
        Assert.Equal(TimeSpan.Zero, observation.CapturedAt.Offset);
        Assert.Equal(TimeSpan.Zero, observation.ReceivedAt.Offset);
    }

    [Theory]
    [InlineData("0000-bbb", true)]
    [InlineData("synthetic-invalid", false)]
    public void ProjectionMapsExistingEventsWithoutDiscardingInvalidObservations(string plate, bool expectedValidity)
    {
        var original = new LprEvent(EventId, DeviceId, "synthetic-site", StayTestData.Monday,
            StayTestData.Monday.AddSeconds(1), plate, 0.01m, LprDirection.Entry);
        var projection = StayObservation.FromEvent(original);
        Assert.Equal(original.EventId, projection.EventId);
        Assert.Equal(original.PlateNormalized, projection.PlateNormalized);
        Assert.Equal(original.PlateRulesVersion, projection.PlateRulesVersion);
        Assert.Equal(expectedValidity, projection.IsPlateValid);
        Assert.Equal(original.Direction, projection.Direction);
        if (!expectedValidity)
            Assert.Throws<ArgumentException>(() => StayBoundary.Observed(projection, StayBoundarySource.Inferred));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void ProjectionRejectsConfidenceOutsideTheEventContract(int confidence) =>
        Assert.Throws<ArgumentException>(() => Observation(confidence: confidence));

    [Fact]
    public void ProjectionAndBoundaryRejectMalformedIdentityMetadataOrSource()
    {
        Assert.Throws<ArgumentException>(() => Observation(eventId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Observation(deviceId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Observation(workshop: ""));
        Assert.Throws<ArgumentException>(() => Observation(plate: ""));
        Assert.Throws<ArgumentException>(() => Observation(rules: ""));
        Assert.Throws<ArgumentException>(() => Observation(confidence: 0.000000001m));
        Assert.Throws<ArgumentException>(() => Observation(direction: (LprDirection)99));
        Assert.Throws<ArgumentNullException>(() => StayObservation.FromEvent(null!));
        Assert.Throws<ArgumentNullException>(() => StayBoundary.Observed(null!, StayBoundarySource.Explicit));
        Assert.Throws<ArgumentException>(() => StayBoundary.Observed(Observation(), StayBoundarySource.Estimated));
        Assert.Throws<ArgumentException>(() => StayBoundary.Observed(Observation(), (StayBoundarySource)99));
    }

    [Fact]
    public void EstimatedBoundaryNormalizesTheInstantWithoutCreatingAnObservation()
    {
        var estimate = StayBoundary.Estimated(StayTestData.Monday.ToOffset(TimeSpan.FromHours(1)));
        Assert.Equal(StayTestData.Monday, estimate.At);
        Assert.Equal(TimeSpan.Zero, estimate.At.Offset);
        Assert.Equal(StayBoundarySource.Estimated, estimate.Source);
        Assert.Null(estimate.Observation);
        Assert.Null(estimate.EventId);
    }

    private static StayObservation Observation(Guid? eventId = null, Guid? deviceId = null,
        string workshop = "synthetic-site", string plate = "1234BCD", string rules = "saved-v1",
        decimal confidence = 0.5m, LprDirection? direction = null) =>
        new(eventId ?? EventId, deviceId ?? DeviceId, workshop, plate, StayTestData.Monday,
            StayTestData.Monday.AddSeconds(1), true, rules, confidence, direction);
}
