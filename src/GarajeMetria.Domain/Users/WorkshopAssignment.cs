using System.Buffers;
using System.Text;
using GarajeMetria.Domain.Authentication;

namespace GarajeMetria.Domain.Users;

public static class AssignmentRules
{
    public static bool ValidText(string? value, int maximum, bool identifier = false)
    {
        if (string.IsNullOrWhiteSpace(value) || identifier && value != value.Trim()) return false;
        var span = value.AsSpan();
        var count = 0;
        while (!span.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(span, out var rune, out var consumed) != OperationStatus.Done ||
                Rune.IsControl(rune) || ++count > maximum) return false;
            span = span[consumed..];
        }
        return true;
    }

    public static bool CanRemove(string? targetRole) => targetRole == UserRoles.Administrator;
}

public sealed class WorkshopAssignmentAuditEntry
{
    private WorkshopAssignmentAuditEntry() { }

    public WorkshopAssignmentAuditEntry(Guid actorId, Guid userId, string? previousWorkshopId,
        string? newWorkshopId, string reason, DateTimeOffset occurredAt, string correlationId)
    {
        if (actorId == Guid.Empty || userId == Guid.Empty || previousWorkshopId == newWorkshopId ||
            !AssignmentRules.ValidText(reason, 500) || occurredAt.Offset != TimeSpan.Zero ||
            !AssignmentRules.ValidText(correlationId, 128, true) ||
            previousWorkshopId is not null && !AssignmentRules.ValidText(previousWorkshopId, 128, true) ||
            newWorkshopId is not null && !AssignmentRules.ValidText(newWorkshopId, 128, true))
            throw new ArgumentException("Invalid workshop assignment audit.");
        Id = Guid.NewGuid();
        ActorId = actorId;
        UserId = userId;
        PreviousWorkshopId = previousWorkshopId;
        NewWorkshopId = newWorkshopId;
        Reason = reason;
        OccurredAt = occurredAt;
        CorrelationId = correlationId;
    }

    public Guid Id { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid UserId { get; private set; }
    public string? PreviousWorkshopId { get; private set; }
    public string? NewWorkshopId { get; private set; }
    public string Reason { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
    public string CorrelationId { get; private set; } = "";
}
