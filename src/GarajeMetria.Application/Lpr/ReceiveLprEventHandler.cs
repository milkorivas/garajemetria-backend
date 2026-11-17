using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Lpr;
using GarajeMetria.Domain.Stays;
using MediatR;

namespace GarajeMetria.Application.Lpr;

public sealed class ReceiveLprEventHandler(ILprEventRepository events, ILprUnitOfWork unitOfWork, DeviceAccess access, LprOptions options,
    IStayRepository stays, IStayRecalculationService calculation, IObservationLocationWriter locations)
    : IRequestHandler<ReceiveLprEventCommand, Outcome<ReceiveLprEventResult>>
{
    public async Task<Outcome<ReceiveLprEventResult>> Handle(ReceiveLprEventCommand request, CancellationToken ct)
    {
        try { return await ExecuteAsync(request, allowInsert: true, ct); }
        catch (LprEventIdentityCollisionException)
        {
            // The failed insert has rolled back. Reconcile once with fresh authorization;
            // this path never repeats a write and cannot turn an uncertain commit into success.
            return await ExecuteAsync(request, allowInsert: false, ct);
        }
    }

    private Task<Outcome<ReceiveLprEventResult>> ExecuteAsync(ReceiveLprEventCommand request, bool allowInsert, CancellationToken ct) =>
        unitOfWork.ExecuteAsync(new(null, request.DeviceId, request.EventId), async token =>
        {
            var check = await access.CheckAtAsync(request.DeviceId, request.WorkshopId, token);
            if (!check.Access.Succeeded) return Outcome<ReceiveLprEventResult>.Fail(check.Access.Error!.Code);
            var match = await events.CompareAsync(request, token);
            if (match.Comparison == LprEventComparison.Identical)
                return Outcome<ReceiveLprEventResult>.Success(new(true, match.Receipt!));
            if (match.Comparison == LprEventComparison.Conflict)
                return Outcome<ReceiveLprEventResult>.Fail(FailureCode.EventConflict);
            if (!allowInsert) return Outcome<ReceiveLprEventResult>.Fail(FailureCode.InternalError);
            var window = new LprCaptureWindow(TimeSpan.FromMinutes(options.MaxEventAgeMinutes), TimeSpan.FromMinutes(options.MaxFutureMinutes));
            if (!window.Contains(request.CapturedAt, check.Now))
                return Outcome<ReceiveLprEventResult>.Fail(FailureCode.CaptureOutsideWindow);
            var observation = new LprEvent(request.EventId, request.DeviceId, request.WorkshopId, request.CapturedAt,
                check.Now, request.PlateOriginal, request.Confidence, request.Direction);
            events.Add(observation);
            var supplied = StayObservation.FromEvent(observation);
            await locations.CaptureAsync(supplied, token);
            if (observation.PlateStatus == "valid")
            {
                var partition = await stays.GetOrCreatePartitionAsync(observation.WorkshopId, observation.PlateNormalized, token);
                partition = await stays.RegisterInputAsync(partition.Id, observation.EventId, partition.InputRevision, token);
                await calculation.RecalculateAsync(partition, observation.CapturedAt, check.Now, StayProjectionCause.Ingestion, supplied, token);
            }
            return Outcome<ReceiveLprEventResult>.Success(new(false, new(observation.EventId, observation.ReceivedAt, observation.PlateStatus)));
        }, ct);
}
