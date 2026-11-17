using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Stays;
using Microsoft.EntityFrameworkCore;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class ObservationLocationWriter(AuthenticationDbContext db) : IObservationLocationWriter
{
    public async Task CaptureAsync(StayObservation observation, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Location capture requires a transaction.");
        if (await db.LprObservationLocations.AnyAsync(x => x.EventId == observation.EventId, ct)) return;
        var future = observation.CapturedAt > observation.ReceivedAt;
        var cutoff = future ? observation.ReceivedAt : observation.CapturedAt;
        var location = await db.LprDeviceLocations.AsNoTracking().Where(x => x.DeviceId == observation.DeviceId &&
            x.WorkshopId == observation.WorkshopId && x.RecordedAt <= cutoff)
            .OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.CreationOrder).FirstOrDefaultAsync(ct);
        if (location is not null && location.WorkshopId != observation.WorkshopId)
            throw new InvalidOperationException("Location belongs to another workshop.");
        db.LprObservationLocations.Add(new()
        {
            EventId = observation.EventId,
            DeviceId = observation.DeviceId,
            WorkshopId = observation.WorkshopId,
            LocationId = location?.Id,
            Provenance = location is null ? ObservationLocationProvenance.Unknown :
                future ? ObservationLocationProvenance.LatestKnownForFutureCapture : ObservationLocationProvenance.AtCapture
        });
    }
}
