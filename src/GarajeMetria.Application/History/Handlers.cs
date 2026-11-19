using System.Diagnostics;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Stays;
using GarajeMetria.Application.Users;
using GarajeMetria.Domain.Authentication;
using MediatR;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Application.History;

public sealed class HistoryAccess(ICurrentUserContext context, IUserAccessQuery users,
    IAuthenticationSessionRepository sessions, IHistoryCatalogQuery catalog, TimeProvider clock)
{
    public async Task<Outcome<HistoryScope>> ResolveAsync(string[] requested, CancellationToken ct)
    {
        var actor = context.User;
        if (actor is null) return Outcome<HistoryScope>.Fail(FailureCode.SessionEnded);
        var session = await sessions.FindAsync(actor.SessionId, ct);
        if (session?.Allows(actor.Id, actor.Role, clock.GetUtcNow()) != true) return Outcome<HistoryScope>.Fail(FailureCode.SessionEnded);
        if (actor.Role is not (UserRoles.Administrator or UserRoles.Advisor)) return Outcome<HistoryScope>.Fail(FailureCode.Forbidden);
        if (actor.Role == UserRoles.Advisor)
        {
            var user = await users.FindAsync(actor.Id, ct);
            if (user is null) return Outcome<HistoryScope>.Fail(FailureCode.SessionEnded);
            if (AccessDecision.Check(user, actor.Role) is { } error) return Outcome<HistoryScope>.Fail(error);
            if (requested.Any(x => !string.Equals(x, user.DefaultWorkshopId, StringComparison.Ordinal)))
                return Outcome<HistoryScope>.Fail(FailureCode.Forbidden);
            return Outcome<HistoryScope>.Success(new(actor, [user.DefaultWorkshopId!], false));
        }
        var workshops = await catalog.ResolveAsync(requested.Distinct(StringComparer.Ordinal).ToArray(), ct);
        return workshops is null ? Outcome<HistoryScope>.Fail(FailureCode.ResourceNotFound) :
            Outcome<HistoryScope>.Success(new(actor, workshops, requested.Length == 0));
    }
}

public sealed class HistoryService(HistoryAccess access, IHistoryReadSession readSession, IHistoryQuery query,
    IHistoryClosureDiscovery discovery, IHistoryAccessAuditStore audits, IHistoryWorkbookWriter writer,
    IHistoryAdmission admission, HistoryTime time, ILprClock clock, ISender sender, ICurrentUserContext context, HistoryOptions options,
    ILogger<HistoryService> logger)
{
    public async Task<Outcome<HistoryPage>> ReadAsync(HistoryRequest request, CancellationToken ct)
    {
        var result = await ExecuteAsync(request, false, ct);
        return result.Error is null ? Outcome<HistoryPage>.Success(result.Page!) : new(null, result.Error);
    }
    public async Task<Outcome<HistoryExport>> ExportAsync(ExportHistoryCommand request, CancellationToken ct)
    {
        var result = await ExecuteAsync(HistoryRequests.Create(request.Kind, request.Filter), true, ct);
        return result.Error is null ? Outcome<HistoryExport>.Success(result.Export!) : new(null, result.Error);
    }
    private sealed record Result(HistoryPage? Page = null, HistoryExport? Export = null, Failure? Error = null);
    private async Task<Result> ExecuteAsync(HistoryRequest request, bool export, CancellationToken original)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(original);
        budget.CancelAfter(TimeSpan.FromSeconds(export ? options.ExportTimeoutSeconds : options.QueryTimeoutSeconds));
        var ct = budget.Token;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var auth = await access.ResolveAsync(request.Filter.WorkshopIds, ct);
            if (!auth.Succeeded) return new(Error: auth.Error);
            var scope = auth.Value!;
            var permit = await admission.AcquireAsync(scope.Actor.Id, export, ct);
            if (!permit.Succeeded) return new(Error: permit.Error);
            await using var lease = permit.Value!;
            logger.LogInformation("History stage: stage={Stage}, elapsedMs={ElapsedMs}", "Admitted", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var now = await clock.GetUtcNowAsync(ct);
            var window = time.Resolve(request, now);
            if (request.Kind is HistoryKind.Events or HistoryKind.Stays or HistoryKind.Timeline && window is null)
                return new(Error: new(FailureCode.InvalidInput));
            Outcome<HistoryPage>? page = null;
            // Work is bounded; the supervised worker continues larger backlogs between requests.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var due = await discovery.ReadDueAsync(request, scope, window, now, ct);
                foreach (var work in due)
                {
                    var closed = await sender.Send(new CloseStayDayCommand(work.PartitionId, work.LocalDate), ct);
                    if (!closed.Succeeded) return new(Error: closed.Error);
                }
                logger.LogInformation("History stage: stage={Stage}, elapsedMs={ElapsedMs}", "Reading", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                page = await readSession.ExecuteAsync(async token =>
                {
                    var current = await access.ResolveAsync(request.Filter.WorkshopIds, token);
                    if (!current.Succeeded) return new Outcome<HistoryPage>(null, current.Error);
                    if (!SameScope(scope, current.Value!)) return Outcome<HistoryPage>.Fail(FailureCode.Forbidden);
                    now = await clock.GetUtcNowAsync(token);
                    if ((await discovery.ReadDueAsync(request, scope, window, now, token)).Count != 0)
                        return Outcome<HistoryPage>.Fail(FailureCode.HistoryProjectionPending, 1);
                    return await query.ReadAsync(request, scope, window, now, export, token);
                }, ct);
                if (page.Error?.Code != FailureCode.HistoryProjectionPending) break;
            }
            if (page?.Succeeded != true) return new(Error: page?.Error ?? new(FailureCode.HistoryProjectionPending, 1));
            logger.LogInformation("History stage: stage={Stage}, rows={Rows}, elapsedMs={ElapsedMs}", "Read", page.Value!.Items.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            HistoryExport? file = null;
            if (export)
            {
                if (page.Value!.TotalCount > 10000) return new(Error: new(FailureCode.HistoryExportLimitExceeded));
                var written = await writer.WriteAsync(page.Value, request.Kind, ct);
                if (!written.Succeeded) return new(Error: written.Error);
                file = written.Value;
                logger.LogInformation("History stage: stage={Stage}, elapsedMs={ElapsedMs}", "Workbook", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            var final = await access.ResolveAsync(request.Filter.WorkshopIds, ct);
            if (!final.Succeeded) return new(Error: final.Error);
            if (!SameScope(scope, final.Value!)) return new(Error: new(FailureCode.Forbidden));
            try
            {
                await audits.AppendAsync(new(Guid.NewGuid(), scope, await clock.GetUtcNowAsync(ct),
                    (export ? "Export" : "Read") + request.Kind, page.Value!.Items.Count, context.CorrelationId), ct);
            }
            catch (PersistenceFailureException ex)
            {
                return new(Error: new(ex.Code == FailureCode.OperationOutcomeUnknown ? ex.Code : FailureCode.HistoryAuditUnavailable));
            }
            return new(page.Value, file);
        }
        catch (OperationCanceledException) when (!original.IsCancellationRequested)
        { return new(Error: new(FailureCode.ServiceUnavailable)); }
    }
    private static bool SameScope(HistoryScope first, HistoryScope second) => first.Actor == second.Actor &&
        first.WorkshopIds.ToHashSet(StringComparer.Ordinal).SetEquals(second.WorkshopIds);
}

public sealed class HistoryHandlers(HistoryService service) :
    IRequestHandler<SearchLprEventsQuery, Outcome<HistoryPage>>, IRequestHandler<SearchStaysQuery, Outcome<HistoryPage>>,
    IRequestHandler<ListPresentVehiclesQuery, Outcome<HistoryPage>>, IRequestHandler<GetVehicleTimelineQuery, Outcome<HistoryPage>>,
    IRequestHandler<GetLprEventQuery, Outcome<HistoryPage>>, IRequestHandler<GetStayQuery, Outcome<HistoryPage>>,
    IRequestHandler<ListStayObservationsQuery, Outcome<HistoryPage>>, IRequestHandler<ListHistoryDevicesQuery, Outcome<HistoryPage>>,
    IRequestHandler<ListHistoryLocationsQuery, Outcome<HistoryPage>>, IRequestHandler<ExportHistoryCommand, Outcome<HistoryExport>>
{
    public Task<Outcome<HistoryPage>> Handle(SearchLprEventsQuery r, CancellationToken ct) => service.ReadAsync(r, ct);
    public Task<Outcome<HistoryPage>> Handle(SearchStaysQuery r, CancellationToken ct) => service.ReadAsync(r, ct);
    public Task<Outcome<HistoryPage>> Handle(ListPresentVehiclesQuery r, CancellationToken ct) => service.ReadAsync(r, ct);
    public Task<Outcome<HistoryPage>> Handle(GetVehicleTimelineQuery r, CancellationToken ct) => service.ReadAsync(r, ct);
    public Task<Outcome<HistoryPage>> Handle(GetLprEventQuery r, CancellationToken ct) => service.ReadAsync(r, ct);
    public Task<Outcome<HistoryPage>> Handle(GetStayQuery r, CancellationToken ct) => service.ReadAsync(r, ct);
    public Task<Outcome<HistoryPage>> Handle(ListStayObservationsQuery r, CancellationToken ct) => service.ReadAsync(r, ct);
    public Task<Outcome<HistoryPage>> Handle(ListHistoryDevicesQuery r, CancellationToken ct) => service.ReadAsync(r, ct);
    public Task<Outcome<HistoryPage>> Handle(ListHistoryLocationsQuery r, CancellationToken ct) => service.ReadAsync(r, ct);
    public Task<Outcome<HistoryExport>> Handle(ExportHistoryCommand r, CancellationToken ct) => service.ExportAsync(r, ct);
}
