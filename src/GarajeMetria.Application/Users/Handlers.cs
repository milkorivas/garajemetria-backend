using FluentValidation;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Domain.Authentication;
using GarajeMetria.Domain.Users;
using MediatR;

namespace GarajeMetria.Application.Users;

public sealed record ListLocalUsersQuery(int PageNumber, int PageSize) : IRequest<Outcome<LocalUserPage>>;
public sealed record GetDefaultWorkshopQuery(string UserId) : IRequest<Outcome<AssignmentResult>>;
public sealed record SetDefaultWorkshopCommand(string UserId, string? WorkshopId, string Reason, Guid ExpectedVersion)
    : IRequest<Outcome<AssignmentResult>>
{
    public override string ToString() => nameof(SetDefaultWorkshopCommand);
}

public sealed class ListLocalUsersValidator : AbstractValidator<ListLocalUsersQuery>
{
    public ListLocalUsersValidator(UserAdministrationOptions options)
    {
        RuleFor(x => x.PageNumber).GreaterThan(0);
        RuleFor(x => x.PageSize).InclusiveBetween(1, options.MaxPageSize);
    }
}

public sealed class GetDefaultWorkshopValidator : AbstractValidator<GetDefaultWorkshopQuery>
{
    public GetDefaultWorkshopValidator() => RuleFor(x => x.UserId).Must(x => AssignmentRules.ValidText(x, 128, true));
}

public sealed class SetDefaultWorkshopValidator : AbstractValidator<SetDefaultWorkshopCommand>
{
    public SetDefaultWorkshopValidator()
    {
        RuleFor(x => x.UserId).Must(x => AssignmentRules.ValidText(x, 128, true));
        RuleFor(x => x.WorkshopId).Must(x => x is null || AssignmentRules.ValidText(x, 128, true));
        RuleFor(x => x.Reason).Must(x => AssignmentRules.ValidText(x, 500));
        RuleFor(x => x.ExpectedVersion).NotEmpty();
    }
}

public sealed class UserAdministrationAccess(ICurrentUserContext context, IAuthenticationSessionRepository sessions, TimeProvider clock)
{
    public async Task<FailureCode?> CheckAsync(CancellationToken cancellationToken)
    {
        var actor = context.User;
        if (actor is null) return FailureCode.SessionEnded;
        var session = await sessions.FindAsync(actor.SessionId, cancellationToken);
        if (session?.Allows(actor.Id, actor.Role, clock.GetUtcNow()) != true) return FailureCode.SessionEnded;
        return actor.Role == UserRoles.Administrator ? null : FailureCode.Forbidden;
    }
}

public sealed class ListLocalUsersHandler(IUserAdministrationQuery users, UserAdministrationAccess access)
    : IRequestHandler<ListLocalUsersQuery, Outcome<LocalUserPage>>
{
    public async Task<Outcome<LocalUserPage>> Handle(ListLocalUsersQuery request, CancellationToken cancellationToken) =>
        await access.CheckAsync(cancellationToken) is { } error ? Outcome<LocalUserPage>.Fail(error) :
            Outcome<LocalUserPage>.Success(await users.ListAsync(request.PageNumber, request.PageSize, cancellationToken));
}

public sealed class GetDefaultWorkshopHandler(IUserAdministrationQuery users, UserAdministrationAccess access)
    : IRequestHandler<GetDefaultWorkshopQuery, Outcome<AssignmentResult>>
{
    public async Task<Outcome<AssignmentResult>> Handle(GetDefaultWorkshopQuery request, CancellationToken cancellationToken)
    {
        if (await access.CheckAsync(cancellationToken) is { } error) return Outcome<AssignmentResult>.Fail(error);
        var state = await users.FindAsync(request.UserId, cancellationToken);
        return state is null ? Outcome<AssignmentResult>.Fail(FailureCode.ResourceNotFound) :
            Outcome<AssignmentResult>.Success(new(state.UserId.ToString(), state.WorkshopId, state.Version));
    }
}

public sealed class SetDefaultWorkshopHandler(IUserAdministrationQuery users, ILocalUserRepository repository,
    IWorkshopQuery workshops, IWorkshopAssignmentAuditRepository audits, ICorporateDirectory directory,
    IAuthenticationSessionRepository sessions, IUnitOfWork unitOfWork, UserAdministrationAccess access,
    ICurrentUserContext context, TimeProvider clock) : IRequestHandler<SetDefaultWorkshopCommand, Outcome<AssignmentResult>>
{
    public async Task<Outcome<AssignmentResult>> Handle(SetDefaultWorkshopCommand request, CancellationToken cancellationToken)
    {
        if (await access.CheckAsync(cancellationToken) is { } error) return Outcome<AssignmentResult>.Fail(error);
        var initial = await users.FindAsync(request.UserId, cancellationToken);
        if (initial is null) return Outcome<AssignmentResult>.Fail(FailureCode.ResourceNotFound);
        if (initial.Version != request.ExpectedVersion) return Outcome<AssignmentResult>.Fail(FailureCode.PreconditionFailed);
        // AD I/O must complete before opening the local transaction, even for a null no-op.
        if (request.WorkshopId is null)
        {
            var target = await directory.ReadAsync(initial.DirectoryObjectId, cancellationToken);
            if (target.Status == DirectoryStatus.Unavailable || target.Status == DirectoryStatus.Valid &&
                (target.Identity is null || target.Identity.ObjectId != initial.DirectoryObjectId))
                return Outcome<AssignmentResult>.Fail(FailureCode.DirectoryUnavailable);
            if (target.Status != DirectoryStatus.Valid || !AssignmentRules.CanRemove(target.Identity?.Role))
                return Outcome<AssignmentResult>.Fail(FailureCode.AssignmentConflict);
        }
        return await unitOfWork.ExecuteAsync(initial.UserId, async ct =>
        {
            if (await access.CheckAsync(ct) is { } denied) return Outcome<AssignmentResult>.Fail(denied);
            var state = await users.FindAsync(request.UserId, ct);
            if (state is null) return Outcome<AssignmentResult>.Fail(FailureCode.ResourceNotFound);
            if (state.Version != request.ExpectedVersion) return Outcome<AssignmentResult>.Fail(FailureCode.PreconditionFailed);
            if (request.WorkshopId is not null)
            {
                var status = await workshops.CheckAsync(request.WorkshopId, ct);
                var failure = status switch
                {
                    WorkshopAvailability.CatalogUnavailable => FailureCode.ServiceUnavailable,
                    WorkshopAvailability.Missing => FailureCode.ResourceNotFound,
                    WorkshopAvailability.Inactive => FailureCode.AssignmentConflict,
                    _ => (FailureCode?)null
                };
                if (failure is { } invalid) return Outcome<AssignmentResult>.Fail(invalid);
            }
            if (state.WorkshopId == request.WorkshopId)
                return Outcome<AssignmentResult>.Success(new(state.UserId.ToString(), state.WorkshopId, state.Version));
            var version = Guid.NewGuid();
            if (!await repository.SetAssignmentAsync(state.UserId, state.Version, request.WorkshopId, version, ct))
                return Outcome<AssignmentResult>.Fail(FailureCode.PreconditionFailed);
            var now = clock.GetUtcNow();
            audits.Add(new(context.User!.Id, state.UserId, state.WorkshopId, request.WorkshopId, request.Reason, now, context.CorrelationId));
            var session = await sessions.FindOpenAsync(state.UserId, ct);
            session?.Close(now, "workshop_assignment_changed");
            return Outcome<AssignmentResult>.Success(new(state.UserId.ToString(), request.WorkshopId, version));
        }, cancellationToken);
    }
}
