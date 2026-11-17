using FluentValidation;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Domain.Stays;
using MediatR;

namespace GarajeMetria.Application.Stays;

public sealed record StayInitialProjectionPage(IReadOnlyList<Guid> EventIds);
public interface IStayMaintenanceStore
{
    // Resume by durable assignment/location presence; no ephemeral offset or assumptions about UUID order.
    Task<StayInitialProjectionPage> ReadPendingAsync(int pageSize, CancellationToken cancellationToken);
    Task<StayObservation?> FindObservationAsync(Guid eventId, CancellationToken cancellationToken);
    Task<bool> IsProjectedAsync(Guid eventId, CancellationToken cancellationToken);
    Task<Outcome<T>> ExecuteAsync<T>(Func<CancellationToken, Task<Outcome<T>>> action, CancellationToken cancellationToken);
}
public sealed record ProjectStayObservationCommand(Guid EventId) : LprRequest<StayRecalculationResult>;
public sealed class ProjectStayObservationValidator : AbstractValidator<ProjectStayObservationCommand>
{
    public ProjectStayObservationValidator() => RuleFor(x => x.EventId).NotEmpty();
}
public sealed class ProjectStayObservationHandler(IStayMaintenanceStore maintenance, IStayRepository stays,
    IStayRecalculationService calculation, IObservationLocationWriter locations, ILprClock clock)
    : IRequestHandler<ProjectStayObservationCommand, Outcome<StayRecalculationResult>>
{
    public Task<Outcome<StayRecalculationResult>> Handle(ProjectStayObservationCommand request, CancellationToken ct) =>
        maintenance.ExecuteAsync(async token =>
        {
            var observation = await maintenance.FindObservationAsync(request.EventId, token);
            if (observation is null) return Outcome<StayRecalculationResult>.Fail(FailureCode.ResourceNotFound);
            var now = await clock.GetUtcNowAsync(token);
            await locations.CaptureAsync(observation, token);
            if (!observation.IsPlateValid || await maintenance.IsProjectedAsync(observation.EventId, token))
                return Outcome<StayRecalculationResult>.Success(new(0, 0, false));
            var partition = await stays.GetOrCreatePartitionAsync(observation.WorkshopId, observation.PlateNormalized, token);
            partition = await stays.RegisterInputAsync(partition.Id, observation.EventId, partition.InputRevision, token);
            // Initial projection progress is durable per partition through revisions and assignment event keys.
            var result = await calculation.RecalculateAsync(partition, observation.CapturedAt, now, StayProjectionCause.InitialProjection, observation, token);
            return Outcome<StayRecalculationResult>.Success(result);
        }, ct);
}

public sealed record StayDiagnosticResult(bool SchemaAvailable, bool PolicyMatches, long DuePartitions, TimeSpan? OldestPendingAge);
public interface IStayDiagnostics
{
    Task<StayDiagnosticResult> ReadAsync(CancellationToken cancellationToken);
}
