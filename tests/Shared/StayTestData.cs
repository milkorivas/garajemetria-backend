namespace GarajeMetria.Testing;

// Test inputs and expected outcomes only: no stay calculator or production policy implementation.
public sealed record StayFixturePolicy(int EstimatedMinutes = 30, int RepeatSeconds = 60, int MaximumHours = 48);
public sealed record StayFixtureReading(Guid Id, DateTimeOffset CapturedAt, string? Direction,
    string Device = "synthetic-access", string Plate = "1234BCD", string Workshop = "synthetic-workshop");
public sealed record StayFixtureCase(string Id, string AcceptanceCriterion,
    IReadOnlyList<StayFixtureReading> Readings, int ExpectedStayCount);
public sealed record StayFixtureIdentity(Guid Id, Guid OriginEventId, DateTimeOffset CreatedAt,
    StayFixturePolicy Policy, IReadOnlyList<Guid> EventIds);

public static class StayTestData
{
    public static DateTimeOffset Monday { get; } = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
    public static StayFixturePolicy OriginalPolicy { get; } = new();
    public static StayFixturePolicy ChangedPolicy { get; } = new(45, 90, 72);

    public static StayFixtureReading Reading(int sequence, DateTimeOffset capturedAt, string? direction) =>
        new(Guid.Parse($"00000000-0000-4000-8000-{sequence:000000000000}"), capturedAt, direction);

    public static IReadOnlyList<StayFixtureCase> Cases { get; } =
    [
        new("separate-days", "CA-023",
            [Reading(1, Monday.AddHours(23).AddMinutes(50), "entry"),
             Reading(2, Monday.AddDays(1).AddMinutes(10), "entry")], 2),
        new("repeat-across-midnight", "CA-024",
            [Reading(1, Monday.AddHours(23).AddMinutes(59).AddSeconds(40), "entry"),
             Reading(2, Monday.AddDays(1).AddSeconds(10), "entry")], 1),
        new("repeat-inclusive", "CA-024",
            [Reading(1, Monday.AddDays(1).AddSeconds(-20), "entry"),
             Reading(2, Monday.AddDays(1).AddSeconds(40), "entry")], 1),
        new("repeat-outside", "CA-024",
            [Reading(1, Monday.AddDays(1).AddSeconds(-20), "entry"),
             Reading(2, Monday.AddDays(1).AddSeconds(40).AddTicks(1), "entry")], 2),
        new("unknown-next-day", "CA-025",
            [Reading(1, Monday.AddHours(23).AddMinutes(50), "entry"),
             Reading(2, Monday.AddDays(1).AddMinutes(5), null)], 1),
        new("unknown-at-maximum", "CA-025",
            [Reading(1, Monday.AddHours(8), "entry"),
             Reading(2, Monday.AddDays(2).AddHours(8), null)], 1),
        new("unknown-before-maximum", "CA-025",
            [Reading(1, Monday.AddHours(8), "entry"),
             Reading(2, Monday.AddDays(2).AddHours(8).AddTicks(-1), null)], 1),
        new("unknown-outside-maximum", "CA-025",
            [Reading(1, Monday.AddHours(8), "entry"),
             Reading(2, Monday.AddDays(2).AddHours(8).AddTicks(1), null)], 2),
        new("near-exit-but-entry-outside", "CA-025",
            [Reading(1, Monday.AddHours(8), "entry"), Reading(2, Monday.AddHours(9), "exit"),
             Reading(3, Monday.AddDays(2).AddHours(8).AddMinutes(30), null)], 2),
        new("equidistant-across-midnight", "CA-025",
            [Reading(1, Monday.AddHours(23).AddMinutes(50), "entry"),
             Reading(2, Monday.AddDays(1), null),
             Reading(3, Monday.AddDays(1).AddMinutes(10), "entry")], 2)
    ];

    // A was created first even though B contains the earlier capture. Policy and ID must come from A.
    public static IReadOnlyList<StayFixtureReading> FusionReadings { get; } =
    [Reading(11, Monday.AddHours(9), "entry"), Reading(12, Monday.AddHours(8), "entry")];
    public static StayFixtureIdentity FusionWinner { get; } = new(
        Guid.Parse("10000000-0000-4000-8000-000000000001"), FusionReadings[0].Id,
        Monday.AddHours(10), OriginalPolicy, [FusionReadings[0].Id]);
    public static StayFixtureIdentity FusionAbsorbed { get; } = new(
        Guid.Parse("10000000-0000-4000-8000-000000000002"), FusionReadings[1].Id,
        Monday.AddHours(11), ChangedPolicy, [FusionReadings[1].Id]);

    // The original anchor is in the later part, so chronological-first cannot decide identity.
    public static IReadOnlyList<StayFixtureReading> DivisionReadings { get; } =
    [Reading(21, Monday.AddHours(8), "entry"), Reading(22, Monday.AddHours(9), "exit"),
     Reading(23, Monday.AddHours(16), "entry"), Reading(24, Monday.AddHours(17), "exit")];
    public static StayFixtureIdentity DivisionOriginal { get; } = new(
        Guid.Parse("10000000-0000-4000-8000-000000000003"), DivisionReadings[2].Id,
        Monday.AddHours(18), OriginalPolicy, DivisionReadings.Select(x => x.Id).ToArray());
    public static IReadOnlyList<IReadOnlyList<Guid>> DivisionParts { get; } =
        [DivisionReadings.Take(2).Select(x => x.Id).ToArray(), DivisionReadings.Skip(2).Select(x => x.Id).ToArray()];
}

public sealed class StayTestTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private long utcTicks = initial.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);
    public void SetUtcNow(DateTimeOffset value) => Interlocked.Exchange(ref utcTicks, value.UtcTicks);
}

// This calendar is test support until IWorkshopCalendar exists in T004-07/T004-09.
public sealed class StayTestCalendar
{
    private readonly TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById("Atlantic/Canary");
    public DateOnly DateAt(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
    public (DateTimeOffset Start, DateTimeOffset End) Bounds(DateOnly date) =>
        (Midnight(date), Midnight(date.AddDays(1)));
    private DateTimeOffset Midnight(DateOnly date) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
}
