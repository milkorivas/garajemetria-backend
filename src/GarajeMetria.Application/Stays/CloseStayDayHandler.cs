using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using MediatR;

namespace GarajeMetria.Application.Stays;

public sealed class CloseStayDayHandler(IStayClosureUnitOfWork unitOfWork, IStayRepository stays, IStayClosureRepository closures,
    IStayRecalculationService calculation, IWorkshopCalendar calendar, ILprClock clock)
    : IRequestHandler<CloseStayDayCommand, Outcome<CloseStayDayResult>>
{
    public Task<Outcome<CloseStayDayResult>> Handle(CloseStayDayCommand request, CancellationToken ct) =>
        unitOfWork.ExecuteAsync(request.PartitionId, async token =>
        {
            var now = await clock.GetUtcNowAsync(token);
            var day = calendar.GetDay(request.LocalDate);
            if (day.End > now) return Outcome<CloseStayDayResult>.Fail(FailureCode.InvalidInput);
            var partition = await stays.FindPartitionAsync(request.PartitionId, token);
            if (partition is null) return Outcome<CloseStayDayResult>.Fail(FailureCode.ResourceNotFound);
            var previous = await closures.FindAsync(partition.Id, request.LocalDate, token);
            if (previous?.ProcessedInputRevision == partition.InputRevision && (partition.NextClosureAt is null || partition.NextClosureAt > now))
                return Outcome<CloseStayDayResult>.Success(new(partition.Id, request.LocalDate, partition.InputRevision, partition.ProjectionRevision, false));
            var result = await calculation.RecalculateAsync(partition, day.Start, now, StayProjectionCause.DailyClosure, null, token);
            await closures.RecordAsync(new(partition.Id, request.LocalDate, partition.InputRevision, now, null), token);
            return Outcome<CloseStayDayResult>.Success(new(partition.Id, request.LocalDate, result.InputRevision, result.ProjectionRevision, result.Changed));
        }, ct);
}
