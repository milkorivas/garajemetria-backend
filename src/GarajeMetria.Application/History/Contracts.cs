using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Stays;
using GarajeMetria.Application.Users;
using MediatR;

namespace GarajeMetria.Application.History;

public enum HistoryKind { Events, Stays, Present, Timeline, Devices, Locations, Event, Stay, Observations }

public sealed record HistoryFilter
{
    public string[] WorkshopIds { get; init; } = [];
    public Guid[] DeviceIds { get; init; } = [];
    public Guid[] LocationIds { get; init; } = [];
    public string? Plate { get; init; }
    public string PlateMatch { get; init; } = "exact";
    public string? OriginalText { get; init; }
    public string OriginalMatch { get; init; } = "exact";
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public DateOnly? FromDate { get; init; }
    public DateOnly? ThroughDate { get; init; }
    public string TimeBasis { get; init; } = "capture";
    public string? Direction { get; init; }
    public string? PlateStatus { get; init; }
    public string Association { get; init; } = "all";
    public string ConfidenceMode { get; init; } = "all";
    public decimal? ConfidenceMin { get; init; }
    public decimal? ConfidenceMax { get; init; }
    public string? State { get; init; }
    public string? EntrySource { get; init; }
    public string? ExitSource { get; init; }
    public string? Search { get; init; }
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public string? Order { get; init; }
    public override string ToString() => nameof(HistoryFilter);
}

public sealed record HistoryWindow(DateTimeOffset Start, DateTimeOffset End);
public sealed record HistoryScope(CurrentUser Actor, string[] WorkshopIds, bool AllAuthorized)
{
    public override string ToString() => nameof(HistoryScope);
}
public sealed record HistoryLabel(string Id, string Name);
public sealed record HistoryEvent(Guid EventId, string PlateOriginal, string? PlateNormalized, string PlateStatus,
    DateTimeOffset CapturedAtUtc, DateTimeOffset ReceivedAtUtc, HistoryLabel Workshop, HistoryLabel Device,
    ObservationLocationDto Location, string? Direction, decimal? Confidence, Guid? StayId)
{
    public override string ToString() => nameof(HistoryEvent);
}
public sealed record HistoryBoundary(DateTimeOffset AtUtc, string Source, Guid? EventId);
public sealed record HistoryStay(Guid StayId, string PlateNormalized, HistoryLabel Workshop, string State,
    HistoryBoundary? Entry, HistoryBoundary? Exit, decimal? DurationSeconds, bool DurationIsEstimated,
    string? DurationUnavailableReason, DateTimeOffset UpdatedAtUtc, long Revision)
{
    public override string ToString() => nameof(HistoryStay);
}
public sealed record HistorySelector(string Id, string Name, string WorkshopId, string? DeviceId,
    DateTimeOffset? RecordedAtUtc)
{
    public override string ToString() => nameof(HistorySelector);
}
public sealed record HistoryItem(string Kind, DateTimeOffset OccurredAtUtc, HistoryEvent? Event = null,
    HistoryStay? Stay = null, HistorySelector? Selector = null)
{
    public override string ToString() => nameof(HistoryItem);
}
public sealed record HistoryPage(IReadOnlyList<HistoryItem> Items, int PageNumber, int PageSize, long TotalCount,
    DateTimeOffset AsOfUtc, DateTimeOffset UpdatedAtUtc, HistoryWindow? Window,
    Guid? RequestedStayId = null, Guid? ResolvedStayId = null)
{
    public long TotalPages => TotalCount / PageSize + (TotalCount % PageSize == 0 ? 0 : 1);
    public bool WasReplaced => RequestedStayId is not null && RequestedStayId != ResolvedStayId;
    public string TimeZone => "Atlantic/Canary";
    public override string ToString() => nameof(HistoryPage);
}
public sealed record HistoryAudit(Guid Id, HistoryScope Scope, DateTimeOffset OccurredAt, string Operation,
    long RowCount, string CorrelationId)
{
    public override string ToString() => nameof(HistoryAudit);
}
public sealed record HistoryExport(byte[] Content, int RowCount = 0)
{
    public override string ToString() => nameof(HistoryExport);
}
public sealed class HistoryOptions
{
    public int QueryTimeoutSeconds { get; set; } = 30;
    public int ExportTimeoutSeconds { get; set; } = 60;
    public int SqlTimeoutSeconds { get; set; } = 15;
    public void Validate()
    {
        if (QueryTimeoutSeconds is < 1 or > 30 || ExportTimeoutSeconds is < 1 or > 60 ||
            SqlTimeoutSeconds is < 1 or > 15 || SqlTimeoutSeconds > QueryTimeoutSeconds)
            throw new InvalidOperationException("Invalid History configuration.");
    }
}

public interface IHistoryCatalogQuery
{
    Task<string[]?> ResolveAsync(string[] requested, CancellationToken cancellationToken);
}
public interface IHistoryReadSession
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);
}
public interface IHistoryQuery
{
    Task<Outcome<HistoryPage>> ReadAsync(HistoryRequest request, HistoryScope scope, HistoryWindow? window,
        DateTimeOffset now, bool export, CancellationToken cancellationToken);
}
public interface IHistoryClosureDiscovery
{
    Task<IReadOnlyList<StayClosureWork>> ReadDueAsync(HistoryRequest request, HistoryScope scope, HistoryWindow? window,
        DateTimeOffset now, CancellationToken cancellationToken);
}
public interface IHistoryAccessAuditStore
{
    Task AppendAsync(HistoryAudit audit, CancellationToken cancellationToken);
}
public interface IHistoryWorkbookWriter
{
    Task<Outcome<HistoryExport>> WriteAsync(HistoryPage page, HistoryKind kind, CancellationToken cancellationToken);
}
public interface IHistoryAdmission
{
    Task<Outcome<IAsyncDisposable>> AcquireAsync(Guid userId, bool export, CancellationToken cancellationToken);
}

public abstract record HistoryRequest(HistoryKind Kind, HistoryFilter Filter, Guid? ResourceId = null)
    : IRequest<Outcome<HistoryPage>>, IFailureResponseRequest<Outcome<HistoryPage>>
{
    public Outcome<HistoryPage> Fail(FailureCode code) => Outcome<HistoryPage>.Fail(code);
    public Outcome<HistoryPage> Fail(Failure failure) => new(null, failure);
    public override string ToString() => GetType().Name;
}
public sealed record SearchLprEventsQuery(HistoryFilter Filters) : HistoryRequest(HistoryKind.Events, Filters)
{ public override string ToString() => nameof(SearchLprEventsQuery); }
public sealed record SearchStaysQuery(HistoryFilter Filters) : HistoryRequest(HistoryKind.Stays, Filters)
{ public override string ToString() => nameof(SearchStaysQuery); }
public sealed record ListPresentVehiclesQuery(HistoryFilter Filters) : HistoryRequest(HistoryKind.Present, Filters)
{ public override string ToString() => nameof(ListPresentVehiclesQuery); }
public sealed record GetVehicleTimelineQuery(HistoryFilter Filters) : HistoryRequest(HistoryKind.Timeline, Filters)
{ public override string ToString() => nameof(GetVehicleTimelineQuery); }
public sealed record ListHistoryDevicesQuery(HistoryFilter Filters) : HistoryRequest(HistoryKind.Devices, Filters)
{ public override string ToString() => nameof(ListHistoryDevicesQuery); }
public sealed record ListHistoryLocationsQuery(HistoryFilter Filters) : HistoryRequest(HistoryKind.Locations, Filters)
{ public override string ToString() => nameof(ListHistoryLocationsQuery); }
public sealed record GetLprEventQuery(Guid Id, HistoryFilter Filters) : HistoryRequest(HistoryKind.Event, Filters, Id)
{ public override string ToString() => nameof(GetLprEventQuery); }
public sealed record GetStayQuery(Guid Id, HistoryFilter Filters) : HistoryRequest(HistoryKind.Stay, Filters, Id)
{ public override string ToString() => nameof(GetStayQuery); }
public sealed record ListStayObservationsQuery(Guid Id, HistoryFilter Filters) : HistoryRequest(HistoryKind.Observations, Filters, Id)
{ public override string ToString() => nameof(ListStayObservationsQuery); }
public sealed record ExportHistoryCommand(HistoryKind Kind, HistoryFilter Filter) : IRequest<Outcome<HistoryExport>>,
    IFailureResponseRequest<Outcome<HistoryExport>>
{
    public Outcome<HistoryExport> Fail(FailureCode code) => Outcome<HistoryExport>.Fail(code);
    public Outcome<HistoryExport> Fail(Failure failure) => new(null, failure);
    public override string ToString() => nameof(ExportHistoryCommand);
}
