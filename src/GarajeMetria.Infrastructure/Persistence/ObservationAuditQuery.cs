using System.Text;
using GarajeMetria.Application.Stays;
using Microsoft.EntityFrameworkCore;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class ObservationAuditQuery(AuthenticationDbContext db) : IObservationAuditQuery
{
    public async Task<ObservationAuditPage> ReadAsync(ObservationAuditWindow window, StayObservationCursor? after, CancellationToken ct)
    {
        var workshop = Encoding.Unicode.GetBytes(window.WorkshopId);
        var original = Encoding.Unicode.GetBytes(window.PlateOriginal ?? "");
        var filterPlate = window.PlateOriginal is not null;
        var hasCursor = after is not null;
        var captured = after?.CapturedAt ?? window.Start;
        var order = after?.EventId.ToByteArray(bigEndian: true) ?? new byte[16];
        var rows = await db.LprEvents.FromSqlInterpolated($"SELECT * FROM [LprEvents] WHERE [DeviceId] = {window.DeviceId} AND CONVERT(varbinary(512), [WorkshopId]) = {workshop} AND ({!filterPlate} = 1 OR CONVERT(varbinary(64), [PlateOriginal]) = {original}) AND [CapturedAt] >= {window.Start} AND [CapturedAt] < {window.End} AND ({!hasCursor} = 1 OR [CapturedAt] > {captured} OR ([CapturedAt] = {captured} AND [EventOrderKey] > {order}))")
            .AsNoTracking().OrderBy(x => x.CapturedAt).ThenBy(x => x.EventOrderKey).Take(window.PageSize + 1).ToArrayAsync(ct);
        var selected = rows.Take(window.PageSize).ToArray();
        var ids = selected.Select(x => x.EventId).ToArray();
        var locations = await (from link in db.LprObservationLocations.AsNoTracking()
                               join location in db.LprDeviceLocations.AsNoTracking() on link.LocationId equals location.Id into versions
                               from location in versions.DefaultIfEmpty()
                               where ids.Contains(link.EventId)
                               select new
                               {
                                   link.EventId,
                                   Value = new ObservationLocationDto(link.LocationId,
                                   location == null ? null : location.Description, location == null ? null : location.RecordedAt, link.Provenance)
                               })
            .ToDictionaryAsync(x => x.EventId, x => x.Value, ct);
        return new(selected.Select(x => new StoredObservationAudit(x.EventId, x.DeviceId, x.WorkshopId, x.CapturedAt, x.ReceivedAt,
            x.PlateOriginal, x.PlateNormalized, x.PlateStatus, x.PlateFormat, x.PlateRulesVersion, x.Confidence, x.Direction,
            locations.GetValueOrDefault(x.EventId) ?? new(null, null, null, ObservationLocationProvenance.Unknown))).ToArray(),
            rows.Length > window.PageSize ? new(selected[^1].CapturedAt, selected[^1].EventId) : null);
    }
}
