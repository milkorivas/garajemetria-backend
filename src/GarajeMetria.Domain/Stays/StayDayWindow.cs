namespace GarajeMetria.Domain.Stays;

// UTC bounds are supplied by the workshop calendar, including 23/25-hour days.
public sealed record StayDayWindow
{
    public StayDayWindow(DateOnly date, DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
            throw new ArgumentException("A stay day requires increasing bounds.");
        Date = date;
        Start = start.ToUniversalTime();
        End = end.ToUniversalTime();
    }

    public DateOnly Date { get; }
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public bool Contains(DateTimeOffset instant) => instant >= Start && instant < End;
}
