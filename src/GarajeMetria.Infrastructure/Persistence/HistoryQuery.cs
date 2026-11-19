using System.Data;
using System.Text.RegularExpressions;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.History;
using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Lpr;
using GarajeMetria.Domain.Stays;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class HistoryQuery(AuthenticationDbContext db) : IHistoryQuery
{
    private const string Ordinal = StayMappingRules.Ordinal;
    private sealed class Candidate
    {
        public Guid Id { get; set; }
        public DateTimeOffset At { get; set; }
        public string Kind { get; set; } = "";
        public byte[] Key { get; set; } = [];
    }
    public async Task<Outcome<HistoryPage>> ReadAsync(HistoryRequest request, HistoryScope scope, HistoryWindow? window,
        DateTimeOffset now, bool export, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("History reads require a read session.");
        var f = request.Filter;
        var events = db.LprEvents.AsNoTracking().Where(x => EF.Parameter(scope.WorkshopIds).Contains(EF.Functions.Collate(x.WorkshopId, Ordinal)));
        var stays = db.VehicleStays.AsNoTracking().Where(x => EF.Parameter(scope.WorkshopIds).Contains(EF.Functions.Collate(x.WorkshopId, Ordinal)) && x.AbsorbedByStayId == null);
        if (!await ValidResourcesAsync(f, scope, ct)) return Outcome<HistoryPage>.Fail(FailureCode.ResourceNotFound);
        Guid? resolvedId = null;
        if (request.Kind is HistoryKind.Stay or HistoryKind.Observations)
        {
            var resolved = await ResolveStayAsync(request.ResourceId!.Value, scope, ct);
            if (resolved is null) return Outcome<HistoryPage>.Fail(FailureCode.ResourceNotFound);
            resolvedId = resolved.Id;
            stays = stays.Where(x => x.Id == resolvedId);
            events = events.Where(x => db.StayObservationAssignments.Any(a => a.EventId == x.EventId && a.WorkshopId == x.WorkshopId && a.StayId == resolvedId));
        }
        if (request.Kind is HistoryKind.Devices or HistoryKind.Locations)
            return Outcome<HistoryPage>.Success(await SelectorsAsync(request, scope, now, ct));
        if (f.Plate is not null)
        {
            var text = HistoryValidator.Normalize(f.Plate);
            var pattern = "%" + Escape(text) + "%";
            events = events.Where(x => x.PlateStatus == "valid" && (f.PlateMatch == "exact" ?
                EF.Functions.Collate(x.PlateNormalized, Ordinal) == text : EF.Functions.Like(EF.Functions.Collate(x.PlateNormalized, Ordinal), pattern, "~")));
            stays = stays.Where(x => f.PlateMatch == "exact" ? EF.Functions.Collate(x.PlateNormalized, Ordinal) == text :
                EF.Functions.Like(EF.Functions.Collate(x.PlateNormalized, Ordinal), pattern, "~"));
        }
        if (request.Kind == HistoryKind.Event) events = events.Where(x => x.EventId == request.ResourceId);
        if (window is not null)
        {
            events = f.TimeBasis == "receipt" ? events.Where(x => x.ReceivedAt >= window.Start && x.ReceivedAt < window.End) :
                events.Where(x => x.CapturedAt >= window.Start && x.CapturedAt < window.End);
            stays = stays.Where(x => x.EntryAt == null ? x.ExitAt >= window.Start && x.ExitAt < window.End :
                x.EntryAt == x.ExitAt ? x.EntryAt >= window.Start && x.EntryAt < window.End :
                x.ExitAt == null ? x.EntryAt < window.End && (now > window.Start || x.EntryAt >= window.Start) :
                x.EntryAt < window.End && x.ExitAt > window.Start);
        }
        if (f.OriginalText is not null)
        {
            var pattern = "%" + Escape(f.OriginalText) + "%";
            events = events.Where(x => f.OriginalMatch == "exact" ? EF.Functions.Collate(x.PlateOriginal, Ordinal) == f.OriginalText && EF.Functions.DataLength(x.PlateOriginal) == f.OriginalText.Length * 2 :
                EF.Functions.Like(EF.Functions.Collate(x.PlateOriginal, Ordinal), pattern, "~"));
        }
        if (f.DeviceIds.Length > 0) events = events.Where(x => f.DeviceIds.Contains(x.DeviceId));
        if (f.LocationIds.Length > 0) events = events.Where(x => db.LprObservationLocations.Any(l => l.EventId == x.EventId &&
            l.WorkshopId == x.WorkshopId && l.LocationId != null && f.LocationIds.Contains(l.LocationId.Value)));
        if (f.PlateStatus is not null) events = events.Where(x => x.PlateStatus == f.PlateStatus);
        if (f.Direction is not null)
            events = f.Direction == "unknown" ? events.Where(x => x.Direction == null) : events.Where(x => x.Direction == (f.Direction == "entry" ? LprDirection.Entry : LprDirection.Exit));
        if (f.ConfidenceMode == "missing") events = events.Where(x => false);
        if (f.ConfidenceMin is not null) events = events.Where(x => x.Confidence >= f.ConfidenceMin);
        if (f.ConfidenceMax is not null) events = events.Where(x => x.Confidence <= f.ConfidenceMax);
        if (f.Association != "all") events = events.Where(x => db.StayObservationAssignments.Any(a =>
            a.EventId == x.EventId && a.WorkshopId == x.WorkshopId && a.StayId != null) == (f.Association == "assigned"));
        if (f.State is not null)
        {
            var state = f.State == "open" ? StayState.Open : f.State == "pendingEntry" ? StayState.PendingEntry : StayState.ClosedProvisional;
            stays = stays.Where(x => x.State == state);
        }
        if (request.Kind == HistoryKind.Present) stays = stays.Where(x => x.State == StayState.Open);
        if (f.EntrySource is not null) { var source = Source(f.EntrySource); stays = stays.Where(x => x.EntrySource == source); }
        if (f.ExitSource is not null) { var source = Source(f.ExitSource); stays = stays.Where(x => x.ExitSource == source); }
        var eventCandidates = events.Select(x => new Candidate { Id = x.EventId, At = x.CapturedAt, Kind = "event", Key = x.EventOrderKey });
        var stayCandidates = stays.Select(x => new Candidate { Id = x.Id, At = (x.EntryAt ?? x.ExitAt)!.Value, Kind = "stay", Key = EF.Property<byte[]>(x, "StayOrderKey") });
        var candidates = request.Kind switch
        {
            HistoryKind.Stays or HistoryKind.Present or HistoryKind.Stay => stayCandidates,
            HistoryKind.Timeline => eventCandidates.Concat(stayCandidates),
            _ => eventCandidates
        };
        var total = await candidates.LongCountAsync(ct);
        if (request.Kind is HistoryKind.Event or HistoryKind.Stay && total == 0) return Outcome<HistoryPage>.Fail(FailureCode.ResourceNotFound);
        var asc = f.Order == "asc" || f.Order is null && request.Kind == HistoryKind.Timeline;
        var ordered = asc ? candidates.OrderBy(x => x.At).ThenBy(x => x.Kind).ThenBy(x => x.Key) :
            candidates.OrderByDescending(x => x.At).ThenByDescending(x => x.Kind).ThenByDescending(x => x.Key);
        var offset = export ? 0 : checked(((long)f.PageNumber - 1) * f.PageSize);
        var count = export ? 10001 : f.PageSize;
        if (offset >= total) return Outcome<HistoryPage>.Success(new([], f.PageNumber, f.PageSize, total, now, now, window, request.ResourceId, resolvedId));
        var page = await PageCandidatesAsync(ordered, offset, count, ct);
        var eventIds = page.Where(x => x.Kind == "event").Select(x => x.Id).ToArray();
        var stayIds = page.Where(x => x.Kind == "stay").Select(x => x.Id).ToArray();
        // Keep the page as a server-side set. A JSON array of 10,000 IDs can receive
        // a tiny cardinality estimate and an expensive nested-loop plan on SQL Server.
        var selected = ordered.Skip(offset <= int.MaxValue ? (int)offset : 0).Take(count);
        var eventSource = offset <= int.MaxValue ? events.Where(x => selected.Where(c => c.Kind == "event").Select(c => c.Id).Contains(x.EventId)) :
            events.Where(x => EF.Parameter(eventIds).Contains(x.EventId));
        var staySource = offset <= int.MaxValue ? stays.Where(x => selected.Where(c => c.Kind == "stay").Select(c => c.Id).Contains(x.Id)) :
            stays.Where(x => EF.Parameter(stayIds).Contains(x.Id));
        var eventRows = await (from e in eventSource
                               join w in db.Workshops on e.WorkshopId equals w.Id
                               join d in db.LprDevices on e.DeviceId equals d.Id
                               join a in db.StayObservationAssignments on e.EventId equals a.EventId into assignments
                               from a in assignments.DefaultIfEmpty()
                               join ol in db.LprObservationLocations on e.EventId equals ol.EventId into locations
                               from ol in locations.DefaultIfEmpty()
                               join l in db.LprDeviceLocations on ol.LocationId equals l.Id into descriptions
                               from l in descriptions.DefaultIfEmpty()
                               select new
                               {
                                   e,
                                   WorkshopName = w.Name,
                                   DeviceName = d.Name,
                                   StayId = a == null ? null : a.StayId,
                                   LocationId = ol == null ? null : ol.LocationId,
                                   Description = l == null ? null : l.Description,
                                   RecordedAt = l == null ? (DateTimeOffset?)null : l.RecordedAt,
                                   Provenance = ol == null ? ObservationLocationProvenance.Unknown : ol.Provenance
                               }).ToArrayAsync(ct);
        var eventDtos = eventRows.ToDictionary(x => x.e.EventId, x => new HistoryEvent(x.e.EventId, x.e.PlateOriginal,
            x.e.PlateStatus == "valid" ? x.e.PlateNormalized : null, x.e.PlateStatus, x.e.CapturedAt, x.e.ReceivedAt,
            new(x.e.WorkshopId, x.WorkshopName), new(x.e.DeviceId.ToString("D"), x.DeviceName),
            new(x.LocationId, x.Description, x.RecordedAt, x.Provenance),
            x.e.Direction == null ? null : x.e.Direction == LprDirection.Entry ? "entry" : "exit", x.e.Confidence, x.StayId));
        var stayRows = await (from s in staySource
                              join w in db.Workshops on s.WorkshopId equals w.Id
                              select new { s, w.Name }).ToArrayAsync(ct);
        var stayDtos = stayRows.ToDictionary(x => x.s.Id, x => Map(x.s, x.Name, now));
        var items = page.Select(x => x.Kind == "event" ? new HistoryItem("event", x.At, Event: eventDtos[x.Id]) :
            new HistoryItem("stay", x.At, Stay: stayDtos[x.Id])).ToArray();
        return Outcome<HistoryPage>.Success(new(items, f.PageNumber, f.PageSize, total, now, now, window,
            request.Kind is HistoryKind.Stay or HistoryKind.Observations ? request.ResourceId : null, resolvedId));
    }
    private static string Escape(string text) => text.Replace("~", "~~", StringComparison.Ordinal).Replace("%", "~%", StringComparison.Ordinal)
        .Replace("_", "~_", StringComparison.Ordinal).Replace("[", "~[", StringComparison.Ordinal);
    private static StayBoundarySource Source(string source) => source == "observed" ? StayBoundarySource.Explicit :
        source == "inferred" ? StayBoundarySource.Inferred : StayBoundarySource.Estimated;
    private static string SourceText(StayBoundarySource? source) => source == StayBoundarySource.Explicit ? "observed" : source == StayBoundarySource.Inferred ? "inferred" : "estimated";
    private static HistoryStay Map(VehicleStayRow s, string name, DateTimeOffset now)
    {
        var duration = HistoryTime.Duration(s.EntryAt, s.ExitAt, now);
        return new(s.Id, s.PlateNormalized, new(s.WorkshopId, name), s.State == StayState.Open ? "open" :
            s.State == StayState.PendingEntry ? "pendingEntry" : "closed",
            s.EntryAt is null ? null : new(s.EntryAt.Value, SourceText(s.EntrySource), s.EntryEventId),
            s.ExitAt is null ? null : new(s.ExitAt.Value, SourceText(s.ExitSource), s.ExitEventId), duration,
            s.EntrySource == StayBoundarySource.Estimated || s.ExitSource == StayBoundarySource.Estimated,
            duration is not null ? null : s.EntryAt is null ? "missingEntry" : "futureEntry", s.UpdatedAt, s.Revision);
    }
    private async Task<VehicleStayRow?> ResolveStayAsync(Guid id, HistoryScope scope, CancellationToken ct)
    {
        var seen = new HashSet<Guid>();
        Guid? partition = null;
        while (seen.Add(id))
        {
            var row = await db.VehicleStays.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id &&
                EF.Parameter(scope.WorkshopIds).Contains(EF.Functions.Collate(x.WorkshopId, Ordinal)), ct);
            if (row is null) return null;
            if (partition is not null && partition != row.PartitionId) throw new InvalidOperationException("Invalid stay lineage.");
            partition = row.PartitionId;
            if (row.AbsorbedByStayId is not { } next) return row;
            if (!await db.StayLineage.AnyAsync(x => x.PartitionId == partition && x.SourceStayId == id && x.TargetStayId == next, ct))
                throw new InvalidOperationException("Invalid stay lineage.");
            id = next;
        }
        throw new InvalidOperationException("Cyclic stay lineage.");
    }
    private async Task<bool> ValidResourcesAsync(HistoryFilter f, HistoryScope scope, CancellationToken ct)
    {
        if (f.DeviceIds.Length > 0)
        {
            var count = await db.LprDevices.LongCountAsync(x => f.DeviceIds.Contains(x.Id) &&
                EF.Parameter(scope.WorkshopIds).Contains(EF.Functions.Collate(x.WorkshopId, Ordinal)), ct);
            if (count != f.DeviceIds.Distinct().Count()) return false;
        }
        if (f.LocationIds.Length > 0)
        {
            var count = await db.LprDeviceLocations.LongCountAsync(x => f.LocationIds.Contains(x.Id) &&
                EF.Parameter(scope.WorkshopIds).Contains(EF.Functions.Collate(x.WorkshopId, Ordinal)), ct);
            if (count != f.LocationIds.Distinct().Count()) return false;
        }
        return true;
    }
    // EF Skip accepts Int32; execute only the small candidate projection with a provider-owned Int64 OFFSET.
    private async Task<List<Candidate>> PageCandidatesAsync(IOrderedQueryable<Candidate> ordered, long offset, int count, CancellationToken ct)
    {
        if (offset <= int.MaxValue) return await ordered.Skip((int)offset).Take(count).ToListAsync(ct);
        await using var command = ordered.Skip(1).Take(count).CreateDbCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        var match = Regex.Match(command.CommandText, @"OFFSET\s+(@\w+)\s+ROWS", RegexOptions.CultureInvariant);
        if (!match.Success) throw new InvalidOperationException("Unsupported history pagination SQL.");
        var parameter = command.Parameters[match.Groups[1].Value];
        parameter.DbType = DbType.Int64; parameter.Value = offset;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<Candidate>();
        while (await reader.ReadAsync(ct)) result.Add(new()
        {
            Id = reader.GetGuid(reader.GetOrdinal("Id")),
            At = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("At")),
            Kind = reader.GetString(reader.GetOrdinal("Kind")),
            Key = (byte[])reader["Key"]
        });
        return result;
    }
    private sealed class SelectorRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string WorkshopId { get; set; } = "";
        public string? DeviceId { get; set; }
        public DateTimeOffset? RecordedAt { get; set; }
    }
    private async Task<HistoryPage> SelectorsAsync(HistoryRequest request, HistoryScope scope, DateTimeOffset now, CancellationToken ct)
    {
        var f = request.Filter;
        IQueryable<SelectorRow> q;
        if (request.Kind == HistoryKind.Devices)
            q = db.LprDevices.AsNoTracking().Where(x => EF.Parameter(scope.WorkshopIds).Contains(EF.Functions.Collate(x.WorkshopId, Ordinal)))
                .Select(x => new SelectorRow { Id = x.Id.ToString(), Name = x.Name, WorkshopId = x.WorkshopId });
        else q = db.LprDeviceLocations.AsNoTracking().Where(x => EF.Parameter(scope.WorkshopIds).Contains(EF.Functions.Collate(x.WorkshopId, Ordinal)) &&
            (f.DeviceIds.Length == 0 || f.DeviceIds.Contains(x.DeviceId)))
            .Select(x => new SelectorRow { Id = x.Id.ToString(), Name = x.Description, WorkshopId = x.WorkshopId, DeviceId = x.DeviceId.ToString(), RecordedAt = x.RecordedAt });
        if (f.Search is not null) q = q.Where(x => x.Name.Contains(f.Search));
        var total = await q.LongCountAsync(ct);
        var offset = ((long)f.PageNumber - 1) * f.PageSize;
        if (offset >= total) return new([], f.PageNumber, f.PageSize, total, now, now, null);
        // Selectors have bounded page counts in practical catalogs; preserve checked arithmetic rather than wrap.
        if (offset > int.MaxValue) throw new PersistenceFailureException(FailureCode.InvalidInput);
        var ordered = request.Kind == HistoryKind.Devices ? q.OrderBy(x => x.Name).ThenBy(x => x.Id) :
            q.OrderBy(x => x.Name).ThenBy(x => x.DeviceId).ThenBy(x => x.Id);
        var rows = await ordered.Skip((int)offset).Take(f.PageSize).ToArrayAsync(ct);
        return new(rows.Select(x => new HistoryItem("selector", now, Selector: new(x.Id, x.Name, x.WorkshopId, x.DeviceId, x.RecordedAt))).ToArray(), f.PageNumber, f.PageSize, total, now, now, null);
    }
}
