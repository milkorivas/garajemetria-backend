namespace GarajeMetria.Domain.Workshops;

public sealed record WorkshopEntry(string Code, string Name);
public sealed record WorkshopState(string Id, string Code, string Name, bool IsActive);
public sealed record WorkshopReconciliation(IReadOnlyList<WorkshopEntry> Added, IReadOnlyList<WorkshopState> Updated,
    IReadOnlyList<string> DeactivatedIds, int Changed, int Reactivated);

public static class WorkshopCatalog
{
    public const string DeactivatedSessionRole = Authentication.UserRoles.Advisor;
    public const string DeactivatedSessionReason = "workshop_deactivated";

    // Codes are opaque: no trimming, case folding, or numeric conversion.
    public static bool IsValid(IReadOnlyList<WorkshopEntry> entries) =>
        entries.All(x => x is not null && !string.IsNullOrWhiteSpace(x.Code) && !string.IsNullOrWhiteSpace(x.Name)) &&
        entries.Select(x => x.Code).Distinct(StringComparer.Ordinal).Count() == entries.Count;

    public static WorkshopReconciliation Reconcile(IReadOnlyList<WorkshopState> existing, IReadOnlyList<WorkshopEntry> incoming)
    {
        if (!IsValid(incoming)) throw new ArgumentException("Invalid complete catalog.", nameof(incoming));
        var byCode = existing.ToDictionary(x => x.Code, StringComparer.Ordinal);
        var present = incoming.Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
        var added = new List<WorkshopEntry>();
        var updated = new List<WorkshopState>();
        var changed = 0;
        var reactivated = 0;
        foreach (var entry in incoming)
        {
            if (!byCode.TryGetValue(entry.Code, out var previous)) { added.Add(entry); continue; }
            if (previous.Name != entry.Name) changed++;
            if (!previous.IsActive) reactivated++;
            updated.Add(previous with { Name = entry.Name, IsActive = true });
        }
        var absent = existing.Where(x => x.IsActive && !present.Contains(x.Code)).ToArray();
        updated.AddRange(absent.Select(x => x with { IsActive = false }));
        return new(added, updated, absent.Select(x => x.Id).ToArray(), changed, reactivated);
    }
}
