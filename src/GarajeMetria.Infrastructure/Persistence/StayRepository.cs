using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Stays;
using GarajeMetria.Infrastructure.Stays;
using Microsoft.EntityFrameworkCore;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed record SavedAssociation(Guid EventId, string GroupKey, Guid? MovementId);
internal sealed record SavedStay(Guid Id, Guid OriginEventId, Guid PolicyId, DateTimeOffset CreatedAt, long CreationOrder,
    DateTimeOffset UpdatedAt, DateTimeOffset? EntryAt, StayBoundarySource? EntrySource, Guid? EntryEventId,
    DateTimeOffset? ExitAt, StayBoundarySource? ExitSource, Guid? ExitEventId, Guid[][] Groups, SavedAssociation[] Associations,
    Guid? AbsorbedBy);
internal sealed record SavedChange(SavedStay? Before, SavedStay After);
internal sealed record SavedRevision(SavedChange[] Changes, StayLineage[] Lineage, Dictionary<string, Guid[]> OriginalGroups);
internal sealed record SavedEvidence(SavedStay Stay, Dictionary<string, Guid[]> OriginalGroups);
internal sealed record SavedRevisionDocument(long Revision, int FormatVersion, string ChangesJson);

internal sealed class StayRepository(AuthenticationDbContext db, StayOptions options, LprPersistenceOptions persistence) : IStayRepository, IStayObservationQuery
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<StayPartition?> FindPartitionAsync(Guid partitionId, CancellationToken ct)
    {
        var row = await db.VehicleStayPartitions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == partitionId, ct);
        return row is null ? null : Partition(row);
    }

    public async Task<StayPartition> GetOrCreatePartitionAsync(string workshopId, string plateNormalized, CancellationToken ct)
    {
        RequireTransaction();
        var workshop = Encoding.Unicode.GetBytes(workshopId);
        var plate = Encoding.Unicode.GetBytes(plateNormalized);
        var row = await db.VehicleStayPartitions.AsNoTracking().SingleOrDefaultAsync(x =>
            EF.Property<byte[]>(x, "WorkshopIdentityKey") == workshop && EF.Property<byte[]>(x, "PlateIdentityKey") == plate, ct);
        var id = row?.Id ?? Guid.NewGuid();
        await SqlTransactions.LockAsync(db, "stays:partition:" + id.ToString("N"), ct, persistence.LockTimeoutSeconds * 1000);
        if (row is null)
        {
            row = new() { Id = id, WorkshopId = workshopId, PlateNormalized = plateNormalized };
            db.VehicleStayPartitions.Add(row);
            await db.SaveChangesAsync(ct);
            db.Entry(row).State = EntityState.Detached;
        }
        return Partition(row);
    }

    public async Task<StayPartition> RegisterInputAsync(Guid partitionId, Guid eventId, long expectedInputRevision, CancellationToken ct)
    {
        RequireTransaction();
        // Flush the newly Added event inside the outer transaction so paginated SQL sees it too.
        await db.SaveChangesAsync(ct);
        var partition = await FindPartitionAsync(partitionId, ct) ?? throw Unavailable();
        var observation = await Events(partition).SingleOrDefaultAsync(x => x.EventId == eventId && x.PlateStatus == "valid", ct);
        if (observation is null || partition.InputRevision != expectedInputRevision) throw Unavailable();
        if (await db.VehicleStayPartitions.Where(x => x.Id == partitionId && x.InputRevision == expectedInputRevision)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.InputRevision, checked(expectedInputRevision + 1)), ct) != 1) throw Unavailable();
        return (await FindPartitionAsync(partitionId, ct))!;
    }

    public async Task<StayObservationPage> ReadAsync(StayReadWindow window, StayObservationCursor? after, CancellationToken ct)
    {
        var workshop = Encoding.Unicode.GetBytes(window.Partition.WorkshopId);
        var plate = Encoding.Unicode.GetBytes(window.Partition.PlateNormalized);
        var captured = after?.CapturedAt ?? window.Start;
        var key = after?.EventId.ToByteArray(bigEndian: true) ?? new byte[16];
        var hasCursor = after is not null;
        var source = window.Direction == StayReadDirection.Forward
            ? db.LprEvents.FromSqlInterpolated($"SELECT * FROM [LprEvents] WHERE CONVERT(varbinary(512), [WorkshopId]) = {workshop} AND CONVERT(varbinary(64), [PlateNormalized]) = {plate} AND [CapturedAt] >= {window.Start} AND [CapturedAt] < {window.End} AND ({!hasCursor} = 1 OR [CapturedAt] > {captured} OR ([CapturedAt] = {captured} AND [EventOrderKey] > {key}))")
            : db.LprEvents.FromSqlInterpolated($"SELECT * FROM [LprEvents] WHERE CONVERT(varbinary(512), [WorkshopId]) = {workshop} AND CONVERT(varbinary(64), [PlateNormalized]) = {plate} AND [CapturedAt] >= {window.Start} AND [CapturedAt] < {window.End} AND ({!hasCursor} = 1 OR [CapturedAt] < {captured} OR ([CapturedAt] = {captured} AND [EventOrderKey] < {key}))");
        var ordered = window.Direction == StayReadDirection.Forward
            ? source.AsNoTracking().OrderBy(x => x.CapturedAt).ThenBy(x => x.EventOrderKey)
            : source.AsNoTracking().OrderByDescending(x => x.CapturedAt).ThenByDescending(x => x.EventOrderKey);
        var rows = await ordered.Take(window.PageSize + 1).ToArrayAsync(ct);
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
        var items = selected.Select(x => new StayObservationDto(Observation(x), locations.GetValueOrDefault(x.EventId) ??
            new(null, null, null, ObservationLocationProvenance.Unknown))).ToArray();
        return new(items, rows.Length > window.PageSize ? new(selected[^1].CapturedAt, selected[^1].EventId) : null);
    }

    public async Task<StayObservation?> ReadAdjacentExplicitAsync(StayPartition partition, DateTimeOffset point, StayReadDirection direction, CancellationToken ct)
    {
        var source = Events(partition).Where(x => x.PlateStatus == "valid" && x.Direction != null);
        var row = direction == StayReadDirection.Backward
            ? await source.Where(x => x.CapturedAt < point).OrderByDescending(x => x.CapturedAt).ThenByDescending(x => x.EventOrderKey).FirstOrDefaultAsync(ct)
            : await source.Where(x => x.CapturedAt > point).OrderBy(x => x.CapturedAt).ThenBy(x => x.EventOrderKey).FirstOrDefaultAsync(ct);
        return row is null ? null : Observation(row);
    }

    public async Task<StayProjectionPage> ReadContextAsync(StayReadWindow window, long? afterCreationOrder, CancellationToken ct)
    {
        var rows = await db.VehicleStays.AsNoTracking().Where(x => x.PartitionId == window.Partition.Id && x.AbsorbedByStayId == null &&
            x.CreationOrder > (afterCreationOrder ?? 0) &&
            ((x.EntryAt == null || x.EntryAt < window.End) && (x.ExitAt == null || x.ExitAt >= window.Start) ||
             db.StayObservationAssignments.Any(a => a.StayId == x.Id && db.LprEvents.Any(e => e.EventId == a.EventId &&
                 e.CapturedAt >= window.Start && e.CapturedAt < window.End))))
            .OrderBy(x => x.CreationOrder).Take(window.PageSize + 1).ToArrayAsync(ct);
        var selected = rows.Take(window.PageSize).ToArray();
        if (selected.Length == 0) return new([], null);
        var evidence = await ReadEvidenceAsync(window.Partition.Id, selected, ct);
        var ids = evidence.Values.SelectMany(ObservationIds).Distinct().ToArray();
        if (ids.Length > options.ContextObservationLimit) throw Unavailable();
        var readings = await ReadObservationsAsync(window.Partition, ids, ct);
        if (readings.Count != ids.Length) throw Unavailable();
        var policyIds = selected.Select(x => x.PolicyId).Distinct().ToArray();
        var policies = await db.StayPolicies.AsNoTracking().Where(x => policyIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var originalGroups = new Dictionary<string, ObservationGroup>(StringComparer.Ordinal);
        ObservationGroup RestoreOriginal(string key, SavedEvidence envelope)
        {
            if (!originalGroups.TryGetValue(key, out var group))
                originalGroups[key] = group = ObservationGroup.Restore(envelope.OriginalGroups[key].Select(id => readings[id]));
            return group;
        }
        var projections = selected.Select(row =>
        {
            if (row.WorkshopId != window.Partition.WorkshopId || row.PlateNormalized != window.Partition.PlateNormalized) throw Unavailable();
            var envelope = evidence[row.Id];
            var saved = envelope.Stay;
            var groups = saved.Groups.Select(g => ObservationGroup.Restore(g.Select(id => readings[id]))).ToArray();
            var associations = saved.Associations.Select(a => UnknownObservationAssociation.Restore(readings[a.EventId],
                RestoreOriginal(a.GroupKey, envelope), a.MovementId is { } movement ? readings[movement] : null));
            var candidate = StayCandidate.Restore(Boundary(row.EntryAt, row.EntrySource, row.EntryEventId, readings),
                Boundary(row.ExitAt, row.ExitSource, row.ExitEventId, readings), StayPolicyRepository.Snapshot(policies[row.PolicyId]), groups, associations);
            return new StayProjection(row.Id, row.OriginEventId, row.CreatedAt, row.CreationOrder, candidate, row.UpdatedAt);
        }).ToArray();
        return new(projections, rows.Length > window.PageSize ? selected[^1].CreationOrder : null);
    }

    public async Task<StayProjectionWriteResult> ApplyAsync(StayProjectionWrite write, CancellationToken ct)
    {
        RequireTransaction();
        var partition = await db.VehicleStayPartitions.SingleAsync(x => x.Id == write.PartitionId, ct);
        if (partition.InputRevision != write.ExpectedInputRevision || partition.ProjectionRevision != write.ExpectedProjectionRevision) throw Unavailable();
        if (!write.Changes.HasChanges)
        {
            await UpdateDeadlinesAsync(write, ct);
            partition.NextClosureAt = await db.VehicleStays.Where(x => x.PartitionId == partition.Id && x.AbsorbedByStayId == null).Select(x => x.NextClosureAt).MinAsync(ct);
            await db.SaveChangesAsync(ct);
            return new(partition.ProjectionRevision, partition.LastCreationOrder);
        }
        var revision = checked(partition.ProjectionRevision + 1);
        var changed = write.Changes.Created.Concat(write.Changes.Updated).ToArray();
        var expectedReadings = changed.SelectMany(p => p.Candidate.Observations.Concat(p.Candidate.UnknownAssociations.SelectMany(a => a.OriginalGroup.Members)))
            .GroupBy(o => o.EventId).ToDictionary(g => g.Key, g => g.Distinct().Single());
        if (expectedReadings.Count > options.ContextObservationLimit) throw Unavailable();
        var actualReadings = await ReadObservationsAsync(Partition(partition), expectedReadings.Keys.ToArray(), ct);
        if (expectedReadings.Count != actualReadings.Count || expectedReadings.Any(x => actualReadings[x.Key] != x.Value))
            throw new InvalidOperationException("Stay evidence differs from the immutable observation source.");
        var policyIds = changed.Select(p => p.Candidate.Policy.Id).Distinct().ToArray();
        var storedPolicies = new Dictionary<Guid, StayPolicySnapshot>();
        foreach (var batch in policyIds.Chunk(500))
            foreach (var row in await db.StayPolicies.AsNoTracking().Where(x => batch.Contains(x.Id)).ToArrayAsync(ct))
                storedPolicies.Add(row.Id, StayPolicyRepository.Snapshot(row));
        if (changed.Any(p => !storedPolicies.TryGetValue(p.Candidate.Policy.Id, out var savedPolicy) || savedPolicy != p.Candidate.Policy))
            throw new InvalidOperationException("A stay must use its complete persisted policy snapshot.");
        var affectedIds = write.Changes.Updated.Concat(write.Changes.Absorbed).Select(x => x.Id).ToArray();
        var old = new Dictionary<Guid, VehicleStayRow>();
        foreach (var batch in affectedIds.Chunk(500))
            foreach (var row in await db.VehicleStays.Where(x => x.PartitionId == partition.Id && batch.Contains(x.Id)).ToArrayAsync(ct)) old.Add(row.Id, row);
        if (old.Count != affectedIds.Distinct().Count() || old.Values.Any(x => x.AbsorbedByStayId != null)) throw Unavailable();
        var previous = await ReadEvidenceAsync(partition.Id, old.Values.ToArray(), ct);
        var originalGroups = new Dictionary<string, Guid[]>(StringComparer.Ordinal);
        foreach (var envelope in previous.Values)
            foreach (var group in envelope.OriginalGroups) originalGroups.TryAdd(group.Key, group.Value);
        var groupKeys = new Dictionary<ObservationGroup, string>(ReferenceEqualityComparer.Instance);
        var records = changed.Select(p => new SavedChange(previous.GetValueOrDefault(p.Id)?.Stay, Saved(p, originalGroups, groupKeys))).ToList();
        foreach (var absorbed in write.Changes.Absorbed)
        {
            var target = write.Changes.AddedLineage.Single(l => l.Kind == StayLineageKind.Fusion && l.SourceId == absorbed.Id).TargetId;
            records.Add(new(previous[absorbed.Id].Stay, previous[absorbed.Id].Stay with { AbsorbedBy = target, UpdatedAt = write.EvaluatedAt }));
        }
        var referencedGroups = records.SelectMany(r => r.After.Associations.Concat(r.Before?.Associations ?? []))
            .Select(a => a.GroupKey).Distinct().ToDictionary(key => key, key => originalGroups[key], StringComparer.Ordinal);
        db.StayProjectionRevisions.Add(new()
        {
            PartitionId = partition.Id,
            WorkshopId = partition.WorkshopId,
            PlateNormalized = partition.PlateNormalized,
            Revision = revision,
            InputRevision = partition.InputRevision,
            Cause = write.Cause,
            EvaluatedAt = write.EvaluatedAt,
            CausingEventId = write.CausingEventId,
            FormatVersion = 1,
            ChangesJson = JsonSerializer.Serialize(new SavedRevision(records.ToArray(), write.Changes.AddedLineage.ToArray(), referencedGroups), Json)
        });
        foreach (var absorbed in write.Changes.Absorbed)
        {
            var row = old[absorbed.Id];
            row.AbsorbedByStayId = records.Single(x => x.After.Id == row.Id).After.AbsorbedBy;
            row.UpdatedAt = write.EvaluatedAt; row.Revision = revision; row.NextClosureAt = null;
        }
        // Free absorbed active anchors before inserting split parts. All phases share the outer transaction.
        await db.SaveChangesAsync(ct);
        foreach (var projection in changed)
        {
            if (projection.Candidate.Observations.Any(o => o.WorkshopId != partition.WorkshopId || o.PlateNormalized != partition.PlateNormalized)) throw Unavailable();
            var isNew = !old.TryGetValue(projection.Id, out var row);
            if (!isNew && (row!.OriginEventId != projection.OriginEventId || row.PolicyId != projection.Candidate.Policy.Id ||
                row.CreatedAt != projection.CreatedAt || row.CreationOrder != projection.CreationOrder))
                throw new InvalidOperationException("An existing stay identity and policy cannot be replaced.");
            if (isNew)
            {
                if (projection.CreationOrder <= partition.LastCreationOrder) throw Unavailable();
                row = new()
                {
                    Id = projection.Id,
                    PartitionId = partition.Id,
                    PolicyId = projection.Candidate.Policy.Id,
                    OriginEventId = projection.OriginEventId,
                    CreatedAt = projection.CreatedAt,
                    CreationOrder = projection.CreationOrder,
                    WorkshopId = partition.WorkshopId,
                    PlateNormalized = partition.PlateNormalized
                };
                db.VehicleStays.Add(row);
            }
            row!.UpdatedAt = projection.UpdatedAt; row.State = projection.Candidate.State;
            row.EntryAt = projection.Candidate.Entry?.At; row.EntrySource = projection.Candidate.Entry?.Source; row.EntryEventId = projection.Candidate.Entry?.EventId;
            row.ExitAt = projection.Candidate.Exit?.At; row.ExitSource = projection.Candidate.Exit?.Source; row.ExitEventId = projection.Candidate.Exit?.EventId;
            row.Revision = revision; row.NextClosureAt = write.NextClosureAt;
        }
        await db.SaveChangesAsync(ct);
        var assignmentIds = changed.SelectMany(p => p.Candidate.Observations).Select(x => x.EventId).ToArray();
        var assignments = new Dictionary<Guid, StayObservationAssignmentRow>();
        foreach (var batch in assignmentIds.Chunk(500))
            foreach (var row in await db.StayObservationAssignments.Where(x => batch.Contains(x.EventId)).ToArrayAsync(ct)) assignments.Add(row.EventId, row);
        foreach (var projection in changed)
        {
            var associationsByEvent = projection.Candidate.UnknownAssociations.ToDictionary(a => a.Observation.EventId);
            foreach (var group in projection.Candidate.Groups)
                foreach (var reading in group.Members)
                {
                    var association = associationsByEvent.GetValueOrDefault(reading.EventId);
                    var original = association?.OriginalGroup ?? group;
                    if (!assignments.TryGetValue(reading.EventId, out var assignment))
                    {
                        assignment = new() { EventId = reading.EventId, PartitionId = partition.Id, WorkshopId = partition.WorkshopId, PlateNormalized = partition.PlateNormalized };
                        db.StayObservationAssignments.Add(assignment);
                    }
                    else if (assignment.PartitionId != partition.Id) throw Unavailable();
                    assignment.StayId = projection.Id; assignment.GroupId = original.EntryRepresentative.EventId;
                    assignment.GroupOriginEventId = original.EntryRepresentative.EventId; assignment.MatchedMovementEventId = association?.MatchedMovement?.EventId;
                    assignment.Role = projection.Candidate.Entry?.EventId == reading.EventId ? StayObservationRole.Entry :
                        projection.Candidate.Exit?.EventId == reading.EventId ? StayObservationRole.Exit :
                        original.Members.Count > 1 && reading != (original.Direction == GarajeMetria.Domain.Lpr.LprDirection.Exit ? original.ExitRepresentative : original.EntryRepresentative)
                            ? StayObservationRole.Repeated : StayObservationRole.Intermediate;
                    assignment.Reason = reading.Direction is not null ? "Explicit" : association?.MatchedMovement is not null ? "NearestMovement" : "DailyFallback";
                    assignment.Revision = revision;
                }
        }
        foreach (var lineage in write.Changes.AddedLineage)
            db.StayLineage.Add(new()
            {
                PartitionId = partition.Id,
                Revision = revision,
                SourceStayId = lineage.SourceId,
                TargetStayId = lineage.TargetId,
                Kind = lineage.Kind,
                RecordedAt = write.EvaluatedAt
            });
        partition.ProjectionRevision = revision;
        partition.LastCreationOrder = Math.Max(partition.LastCreationOrder, write.Changes.Created.Select(x => x.CreationOrder).DefaultIfEmpty().Max());
        await db.SaveChangesAsync(ct);
        await UpdateDeadlinesAsync(write, ct);
        partition.NextClosureAt = await db.VehicleStays.Where(x => x.PartitionId == partition.Id && x.AbsorbedByStayId == null).Select(x => x.NextClosureAt).MinAsync(ct);
        await db.SaveChangesAsync(ct);
        return new(revision, partition.LastCreationOrder);
    }

    private async Task UpdateDeadlinesAsync(StayProjectionWrite write, CancellationToken ct)
    {
        foreach (var group in write.Changes.Active.GroupBy(p => write.NextClosures.TryGetValue(p.Id, out var deadline) ? deadline : write.NextClosureAt))
            foreach (var ids in group.Select(p => p.Id).Chunk(500))
                await db.VehicleStays.Where(x => x.PartitionId == write.PartitionId && x.AbsorbedByStayId == null && ids.Contains(x.Id) && x.NextClosureAt != group.Key)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextClosureAt, group.Key), ct);
    }

    private async Task<Dictionary<Guid, SavedEvidence>> ReadEvidenceAsync(Guid partitionId, VehicleStayRow[] rows, CancellationToken ct)
    {
        if (rows.Length == 0) return [];
        var revisions = rows.Select(x => x.Revision).Distinct().ToArray();
        var documents = new List<SavedRevisionDocument>();
        foreach (var batch in revisions.Chunk(500))
            documents.AddRange(await db.StayProjectionRevisions.AsNoTracking().Where(x => x.PartitionId == partitionId && batch.Contains(x.Revision))
                .Select(x => new SavedRevisionDocument(x.Revision, x.FormatVersion, x.ChangesJson)).ToArrayAsync(ct));
        var parsed = documents.ToDictionary(x => x.Revision, x => x.FormatVersion == 1
            ? JsonSerializer.Deserialize<SavedRevision>(x.ChangesJson, Json) ?? throw Unavailable() : throw Unavailable());
        return rows.ToDictionary(x => x.Id, x => new SavedEvidence(parsed[x.Revision].Changes.Single(c => c.After.Id == x.Id).After, parsed[x.Revision].OriginalGroups));
    }

    private IQueryable<LprEventRow> Events(StayPartition partition)
    {
        var workshop = Encoding.Unicode.GetBytes(partition.WorkshopId);
        var plate = Encoding.Unicode.GetBytes(partition.PlateNormalized);
        return db.LprEvents.FromSqlInterpolated($"SELECT * FROM [LprEvents] WHERE CONVERT(varbinary(512), [WorkshopId]) = {workshop} AND CONVERT(varbinary(64), [PlateNormalized]) = {plate}").AsNoTracking();
    }

    private async Task<Dictionary<Guid, StayObservation>> ReadObservationsAsync(StayPartition partition, Guid[] ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, StayObservation>();
        // Bound provider parameter counts without materializing or truncating the complete context.
        foreach (var batch in ids.Chunk(500))
            foreach (var row in await Events(partition).Where(x => batch.Contains(x.EventId)).ToArrayAsync(ct)) result.Add(row.EventId, Observation(row));
        return result;
    }

    private static IEnumerable<Guid> ObservationIds(SavedEvidence evidence) => evidence.Stay.Groups.SelectMany(g => g)
        .Concat(evidence.Stay.Associations.Select(a => a.GroupKey).Distinct().SelectMany(key => evidence.OriginalGroups[key]))
        .Concat(evidence.Stay.Associations.Where(a => a.MovementId.HasValue).Select(a => a.MovementId!.Value));
    private static SavedStay Saved(StayProjection p, Dictionary<string, Guid[]> groups, Dictionary<ObservationGroup, string> keys) => new(p.Id, p.OriginEventId, p.Candidate.Policy.Id, p.CreatedAt, p.CreationOrder, p.UpdatedAt,
        p.Candidate.Entry?.At, p.Candidate.Entry?.Source, p.Candidate.Entry?.EventId, p.Candidate.Exit?.At, p.Candidate.Exit?.Source, p.Candidate.Exit?.EventId,
        p.Candidate.Groups.Select(g => g.Members.Select(o => o.EventId).ToArray()).ToArray(),
        p.Candidate.UnknownAssociations.Select(a => new SavedAssociation(a.Observation.EventId, GroupKey(a.OriginalGroup, groups, keys), a.MatchedMovement?.EventId)).ToArray(), null);
    private static string GroupKey(ObservationGroup group, Dictionary<string, Guid[]> groups, Dictionary<ObservationGroup, string> keys)
    {
        if (keys.TryGetValue(group, out var key)) return key;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[16];
        foreach (var observation in group.Members)
        {
            observation.EventId.TryWriteBytes(bytes, bigEndian: true, out _);
            hash.AppendData(bytes);
        }
        key = Convert.ToHexString(hash.GetHashAndReset());
        keys[group] = key;
        groups.TryAdd(key, group.Members.Select(o => o.EventId).ToArray());
        return key;
    }
    private static StayBoundary? Boundary(DateTimeOffset? at, StayBoundarySource? source, Guid? eventId, Dictionary<Guid, StayObservation> readings)
    {
        if (at is null) return null;
        if (source == StayBoundarySource.Estimated) return StayBoundary.Estimated(at.Value);
        if (eventId is null || source is null || readings[eventId.Value].CapturedAt != at) throw Unavailable();
        return StayBoundary.Observed(readings[eventId.Value], source.Value);
    }
    internal static StayObservation Observation(LprEventRow x) => new(x.EventId, x.DeviceId, x.WorkshopId, x.PlateNormalized, x.CapturedAt,
        x.ReceivedAt, x.PlateStatus == "valid", x.PlateRulesVersion, x.Confidence, x.Direction);
    internal static StayPartition Partition(VehicleStayPartitionRow row) => new(row.Id, row.WorkshopId, row.PlateNormalized,
        row.InputRevision, row.ProjectionRevision, row.LastCreationOrder, row.NextClosureAt);
    private void RequireTransaction()
    { if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Stay writes require the use-case transaction."); }
    private static PersistenceFailureException Unavailable() => new(FailureCode.ServiceUnavailable);
}
