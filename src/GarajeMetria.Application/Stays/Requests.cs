using FluentValidation;
using GarajeMetria.Application.Lpr;

namespace GarajeMetria.Application.Stays;

// Internal worker request. Partition identity is persisted, never a device or administrator identity.
public sealed record CloseStayDayCommand(Guid PartitionId, DateOnly LocalDate) : LprRequest<CloseStayDayResult>;

public sealed record CloseStayDayResult(Guid PartitionId, DateOnly LocalDate, long ProcessedInputRevision,
    long ProjectionRevision, bool Changed);

public sealed class CloseStayDayValidator : AbstractValidator<CloseStayDayCommand>
{
    public CloseStayDayValidator(IWorkshopCalendar calendar, ILprClock clock)
    {
        RuleFor(x => x.PartitionId).NotEmpty();
        // MinValue is not a supported business day; MaxValue has no representable next midnight.
        RuleFor(x => x.LocalDate).Must(IsSupportedDate).WithMessage("Invalid closure date.");
        RuleFor(x => x.LocalDate).MustAsync(async (date, ct) =>
            calendar.GetDay(date).End <= await clock.GetUtcNowAsync(ct))
            .When(x => x.PartitionId != Guid.Empty && IsSupportedDate(x.LocalDate))
            .WithMessage("The closure day has not ended.");
    }

    private static bool IsSupportedDate(DateOnly date) => date > DateOnly.MinValue && date < DateOnly.MaxValue;
}
