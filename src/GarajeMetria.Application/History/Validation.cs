using FluentValidation;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Lpr;
using MediatR;

namespace GarajeMetria.Application.History;

public interface IHistoryCalendar
{
    DateTime GetLocalTime(DateTimeOffset instant);
}

public sealed class HistoryValidator : AbstractValidator<HistoryRequest>
{
    public HistoryValidator()
    {
        RuleFor(x => x).Must(Valid).WithMessage("Invalid history filter.");
    }
    private static bool Valid(HistoryRequest r)
    {
        var f = r.Filter;
        if (f is null || !Enum.IsDefined(r.Kind) || f.WorkshopIds is null || f.DeviceIds is null || f.LocationIds is null ||
            f.WorkshopIds.Length > 100 || f.WorkshopIds.Any(x => !LprEventRules.ValidText(x, 256)) ||
            f.DeviceIds.Length > 100 || f.LocationIds.Length > 100 || f.DeviceIds.Contains(Guid.Empty) || f.LocationIds.Contains(Guid.Empty) ||
            f.PageNumber < 1 || f.PageSize is < 1 or > 200 || f.Order is not (null or "asc" or "desc") ||
            f.PlateMatch is not ("exact" or "contains") || f.OriginalMatch is not ("exact" or "contains") ||
            f.TimeBasis is not ("capture" or "receipt") || f.Direction is not (null or "entry" or "exit" or "unknown") ||
            f.PlateStatus is not (null or "valid" or "invalid") || f.Association is not ("all" or "assigned" or "unassigned") ||
            f.ConfidenceMode is not ("all" or "range" or "missing") || f.State is not (null or "open" or "pendingEntry" or "closed") ||
            !Source(f.EntrySource) || !Source(f.ExitSource) ||
            f.Plate is not null && (!LprEventRules.ValidText(f.Plate, 32) || string.IsNullOrEmpty(Normalize(f.Plate))) ||
            f.OriginalText is not null && !LprEventRules.ValidText(f.OriginalText, 32) ||
            f.Search is not null && !LprEventRules.ValidText(f.Search, 256) || f.Plate is not null && f.OriginalText is not null ||
            f.From.HasValue != f.To.HasValue || f.FromDate.HasValue != f.ThroughDate.HasValue ||
            f.From.HasValue && f.FromDate.HasValue || f.From >= f.To || f.FromDate > f.ThroughDate ||
            f.FromDate?.Year >= 9999 || f.ThroughDate?.Year >= 9999 || f.From?.Year >= 9999 || f.To?.Year >= 9999 ||
            f.ConfidenceMin is < 0 or > 1 || f.ConfidenceMax is < 0 or > 1 ||
            f.ConfidenceMin > f.ConfidenceMax || !Precision(f.ConfidenceMin) || !Precision(f.ConfidenceMax) ||
            f.ConfidenceMode == "range" && f.ConfidenceMin is null && f.ConfidenceMax is null ||
            f.ConfidenceMode != "range" && (f.ConfidenceMin is not null || f.ConfidenceMax is not null)) return false;
        if (r.Kind == HistoryKind.Timeline && (f.Plate is null || f.PlateMatch != "exact")) return false;
        if (r.Kind is HistoryKind.Event or HistoryKind.Stay && (f.PageNumber != 1 || f.PageSize != 50)) return false;
        if (r.Kind is HistoryKind.Event or HistoryKind.Stay or HistoryKind.Observations &&
            (r.ResourceId is null || r.ResourceId == Guid.Empty)) return false;
        var events = r.Kind == HistoryKind.Events;
        var stays = r.Kind is HistoryKind.Stays or HistoryKind.Present;
        var selector = r.Kind is HistoryKind.Devices or HistoryKind.Locations;
        if (!events && (f.OriginalText is not null || f.Direction is not null || f.PlateStatus is not null ||
            f.Association != "all" || f.ConfidenceMode != "all" || f.TimeBasis != "capture")) return false;
        if (!stays && (f.State is not null || f.EntrySource is not null || f.ExitSource is not null)) return false;
        if (!events && r.Kind != HistoryKind.Locations && f.DeviceIds.Length != 0 || !events && f.LocationIds.Length != 0) return false;
        if (!selector && f.Search is not null) return false;
        if (r.Kind is not (HistoryKind.Events or HistoryKind.Stays or HistoryKind.Timeline) &&
            (f.From is not null || f.FromDate is not null)) return false;
        if (r.Kind is HistoryKind.Event or HistoryKind.Stay or HistoryKind.Observations or HistoryKind.Devices or HistoryKind.Locations && f.Plate is not null) return false;
        return true;
    }
    public static string Normalize(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c) && c != '-')).ToUpperInvariant();
    private static bool Source(string? value) => value is null or "observed" or "inferred" or "estimated";
    private static bool Precision(decimal? value) => value is null || decimal.Round(value.Value, 8) == value;
}

public sealed class HistoryValidationBehavior<TRequest, TResponse>(IValidator<HistoryRequest> validator)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        HistoryRequest? read = request as HistoryRequest;
        if (request is ExportHistoryCommand export)
        {
            if (export.Filter is null || export.Kind is not (HistoryKind.Events or HistoryKind.Stays or HistoryKind.Present or HistoryKind.Timeline) ||
                export.Filter.PageNumber != 1 || export.Filter.PageSize != 50)
                return (TResponse)(object)export.Fail(FailureCode.InvalidInput);
            read = HistoryRequests.Create(export.Kind, export.Filter);
        }
        if (read is not null && !(await validator.ValidateAsync(read, ct)).IsValid)
            return ((IFailureResponseRequest<TResponse>)request).Fail(FailureCode.InvalidInput);
        return await next(ct);
    }
}

public static class HistoryRequests
{
    public static HistoryRequest Create(HistoryKind kind, HistoryFilter f) => kind switch
    {
        HistoryKind.Events => new SearchLprEventsQuery(f),
        HistoryKind.Stays => new SearchStaysQuery(f),
        HistoryKind.Present => new ListPresentVehiclesQuery(f),
        HistoryKind.Timeline => new GetVehicleTimelineQuery(f),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public sealed class HistoryTime(IWorkshopCalendar calendar, IHistoryCalendar local)
{
    public static decimal? Duration(DateTimeOffset? entry, DateTimeOffset? exit, DateTimeOffset now) =>
        entry is null || exit is null && entry > now ? null : ((exit ?? now) - entry.Value).Ticks / (decimal)TimeSpan.TicksPerSecond;
    public HistoryWindow? Resolve(HistoryRequest request, DateTimeOffset now)
    {
        if (request.Kind is not (HistoryKind.Events or HistoryKind.Stays or HistoryKind.Timeline)) return null;
        var f = request.Filter;
        var start = f.From ?? calendar.GetDay(f.FromDate ?? calendar.GetLocalDate(now)).Start;
        var end = f.To ?? calendar.GetDay((f.ThroughDate ?? calendar.GetLocalDate(now)).AddDays(1)).Start;
        var s = local.GetLocalTime(start);
        if (s.Year >= 9999 || start >= end || local.GetLocalTime(end) > s.AddYears(1)) return null;
        return new(start.ToUniversalTime(), end.ToUniversalTime());
    }
    public static bool Overlaps(DateTimeOffset? entry, DateTimeOffset? exit, HistoryWindow w, DateTimeOffset now)
    {
        if (entry is null) return exit >= w.Start && exit < w.End;
        if (exit == entry) return entry >= w.Start && entry < w.End;
        if (exit is null) return entry < w.End && (now > w.Start || entry >= w.Start);
        return entry < w.End && exit > w.Start;
    }
}
