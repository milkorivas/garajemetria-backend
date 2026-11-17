using GarajeMetria.Domain.Lpr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GarajeMetria.Infrastructure.Persistence;

internal static class StayMappingRules
{
    internal const string Ordinal = "Latin1_General_100_BIN2";
    internal const string NonEmptyGuid = "'00000000-0000-0000-0000-000000000000'";

    internal static void Identity<T>(EntityTypeBuilder<T> b) where T : class =>
        b.Property<Guid>("Id").ValueGeneratedNever();

    internal static void Utc<T>(EntityTypeBuilder<T> b, params string[] columns) where T : class
    {
        foreach (var column in columns) b.Property(column).HasPrecision(7);
    }

    internal static void PartitionContext<T>(EntityTypeBuilder<T> b) where T : class
    {
        b.Property<string>("WorkshopId").HasMaxLength(256).IsRequired();
        b.Property<string>("PlateNormalized").HasMaxLength(32).IsRequired();
        b.HasOne<VehicleStayPartitionRow>().WithMany().HasForeignKey("PartitionId", "WorkshopId", "PlateNormalized")
            .HasPrincipalKey(x => new { x.Id, x.WorkshopId, x.PlateNormalized }).OnDelete(DeleteBehavior.Restrict);
    }

    internal static void EventSupport<T>(EntityTypeBuilder<T> b, string eventProperty) where T : class =>
        b.HasOne<LprEventRow>().WithMany().HasForeignKey(eventProperty, "WorkshopId", "PlateNormalized")
            .HasPrincipalKey(x => new { x.EventId, x.WorkshopId, x.PlateNormalized }).OnDelete(DeleteBehavior.Restrict);

    internal static void Immutable<T>(EntityTypeBuilder<T> b, params string[] columns) where T : class
    {
        foreach (var column in columns) b.Property(column).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}

internal sealed class StayPolicyMapping : IEntityTypeConfiguration<StayPolicyRow>
{
    public void Configure(EntityTypeBuilder<StayPolicyRow> b)
    {
        b.ToTable("StayPolicies", t =>
        {
            t.HasCheckConstraint("CK_StayPolicy_Values", "[EstimatedDurationTicks] > 0 AND [RepeatWindowTicks] > 0 AND [MaximumAssociationTicks] > 0 AND [AlgorithmVersion] > 0");
            t.HasCheckConstraint("CK_StayPolicy_Identity", $"[Id] <> {StayMappingRules.NonEmptyGuid} AND LEN([Fingerprint]) = 64");
            t.HasCheckConstraint("CK_StayPolicy_Zone", "[TimeZoneId] = N'Atlantic/Canary'");
        });
        b.HasKey(x => x.Id);
        StayMappingRules.Identity(b);
        b.Property(x => x.Fingerprint).HasMaxLength(64).IsUnicode(false).UseCollation(StayMappingRules.Ordinal).IsRequired();
        b.HasIndex(x => x.Fingerprint).IsUnique();
        b.Property(x => x.TimeZoneId).HasMaxLength(64).UseCollation(StayMappingRules.Ordinal).IsRequired();
        StayMappingRules.Immutable(b, nameof(StayPolicyRow.Fingerprint), nameof(StayPolicyRow.EstimatedDurationTicks),
            nameof(StayPolicyRow.RepeatWindowTicks), nameof(StayPolicyRow.MaximumAssociationTicks),
            nameof(StayPolicyRow.TimeZoneId), nameof(StayPolicyRow.AlgorithmVersion));
    }
}

internal sealed class StayConfigurationMapping : IEntityTypeConfiguration<StayConfigurationRow>
{
    public void Configure(EntityTypeBuilder<StayConfigurationRow> b)
    {
        b.ToTable("StayConfiguration", t =>
        {
            t.HasCheckConstraint("CK_StayConfiguration_Singleton", "[Id] = 1");
            t.HasCheckConstraint("CK_StayConfiguration_Version", $"[Version] <> {StayMappingRules.NonEmptyGuid}");
            t.HasCheckConstraint("CK_StayConfiguration_Utc", "DATEPART(TZOFFSET,[ActivatedAt]) = 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Version).IsConcurrencyToken();
        StayMappingRules.Utc(b, nameof(StayConfigurationRow.ActivatedAt));
        b.HasOne<StayPolicyRow>().WithMany().HasForeignKey(x => x.ActivePolicyId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class VehicleStayPartitionMapping : IEntityTypeConfiguration<VehicleStayPartitionRow>
{
    public void Configure(EntityTypeBuilder<VehicleStayPartitionRow> b)
    {
        b.ToTable("VehicleStayPartitions", t =>
        {
            t.HasCheckConstraint("CK_StayPartition_Identity", $"[Id] <> {StayMappingRules.NonEmptyGuid} AND LEN(LTRIM(RTRIM([WorkshopId]))) > 0 AND LEN(LTRIM(RTRIM([PlateNormalized]))) > 0");
            t.HasCheckConstraint("CK_StayPartition_Revisions", "[InputRevision] >= 0 AND [ProjectionRevision] >= 0 AND [LastCreationOrder] >= 0");
            t.HasCheckConstraint("CK_StayPartition_Utc", "[NextClosureAt] IS NULL OR DATEPART(TZOFFSET,[NextClosureAt]) = 0");
        });
        b.HasKey(x => x.Id);
        StayMappingRules.Identity(b);
        // Match the existing catalog FK collation without altering existing catalog/device/event columns.
        b.Property(x => x.WorkshopId).HasMaxLength(256).IsRequired();
        b.Property(x => x.PlateNormalized).HasMaxLength(32).IsRequired();
        b.HasAlternateKey(x => new { x.Id, x.WorkshopId, x.PlateNormalized });
        // Exact UTF-16 bytes also distinguish trailing spaces, unlike SQL text equality even under BIN2.
        b.Property<byte[]>("WorkshopIdentityKey").HasColumnType("varbinary(512)")
            .HasComputedColumnSql("CONVERT(varbinary(512), [WorkshopId])", stored: true);
        b.Property<byte[]>("PlateIdentityKey").HasColumnType("varbinary(64)")
            .HasComputedColumnSql("CONVERT(varbinary(64), [PlateNormalized])", stored: true);
        // SQL Server cannot use a computed column in a filtered-index predicate.
        b.HasIndex("WorkshopIdentityKey", "PlateIdentityKey").IsUnique().HasFilter(null);
        b.Property(x => x.InputRevision).IsConcurrencyToken();
        b.Property(x => x.ProjectionRevision).IsConcurrencyToken();
        b.HasIndex(x => new { x.NextClosureAt, x.Id });
        StayMappingRules.Utc(b, nameof(VehicleStayPartitionRow.NextClosureAt));
        b.HasOne<WorkshopAccess>().WithMany().HasForeignKey(x => x.WorkshopId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class VehicleStayMapping : IEntityTypeConfiguration<VehicleStayRow>
{
    public void Configure(EntityTypeBuilder<VehicleStayRow> b)
    {
        b.ToTable("VehicleStays", t =>
        {
            t.HasCheckConstraint("CK_Stay_Identity", $"[Id] <> {StayMappingRules.NonEmptyGuid} AND [CreationOrder] > 0 AND [Revision] > 0 AND ([AbsorbedByStayId] IS NULL OR [AbsorbedByStayId] <> [Id])");
            t.HasCheckConstraint("CK_Stay_Chronology", "[UpdatedAt] >= [CreatedAt] AND ([EntryAt] IS NULL OR [ExitAt] IS NULL OR [ExitAt] >= [EntryAt])");
            t.HasCheckConstraint("CK_Stay_State", "([State] = 'Open' AND [EntryAt] IS NOT NULL AND [ExitAt] IS NULL) OR ([State] = 'PendingEntry' AND [EntryAt] IS NULL AND [ExitAt] IS NOT NULL) OR ([State] = 'ClosedProvisional' AND [EntryAt] IS NOT NULL AND [ExitAt] IS NOT NULL)");
            t.HasCheckConstraint("CK_Stay_Entry", "([EntryAt] IS NULL AND [EntrySource] IS NULL AND [EntryEventId] IS NULL) OR ([EntryAt] IS NOT NULL AND [EntrySource] IS NOT NULL AND (([EntrySource] = 'Estimated' AND [EntryEventId] IS NULL) OR ([EntrySource] IN ('Explicit','Inferred') AND [EntryEventId] IS NOT NULL)))");
            t.HasCheckConstraint("CK_Stay_Exit", "([ExitAt] IS NULL AND [ExitSource] IS NULL AND [ExitEventId] IS NULL) OR ([ExitAt] IS NOT NULL AND [ExitSource] IS NOT NULL AND (([ExitSource] = 'Estimated' AND [ExitEventId] IS NULL) OR ([ExitSource] IN ('Explicit','Inferred') AND [ExitEventId] IS NOT NULL)))");
            t.HasCheckConstraint("CK_Stay_Estimation", "([EntrySource] IS NULL OR [EntrySource] <> 'Estimated' OR ([ExitEventId] IS NOT NULL AND [ExitSource] IN ('Explicit','Inferred'))) AND ([ExitSource] IS NULL OR [ExitSource] <> 'Estimated' OR ([EntryEventId] IS NOT NULL AND [EntrySource] IN ('Explicit','Inferred')))");
            t.HasCheckConstraint("CK_Stay_Utc", "DATEPART(TZOFFSET,[CreatedAt]) = 0 AND DATEPART(TZOFFSET,[UpdatedAt]) = 0 AND ([EntryAt] IS NULL OR DATEPART(TZOFFSET,[EntryAt]) = 0) AND ([ExitAt] IS NULL OR DATEPART(TZOFFSET,[ExitAt]) = 0) AND ([NextClosureAt] IS NULL OR DATEPART(TZOFFSET,[NextClosureAt]) = 0)");
        });
        b.HasKey(x => x.Id);
        StayMappingRules.Identity(b);
        b.HasAlternateKey(x => new { x.PartitionId, x.Id });
        b.HasIndex(x => new { x.PartitionId, x.CreationOrder }).IsUnique();
        b.HasIndex(x => x.OriginEventId).IsUnique().HasFilter("[AbsorbedByStayId] IS NULL");
        b.HasIndex(x => new { x.PartitionId, x.EntryAt, x.Id });
        b.HasIndex(x => new { x.PartitionId, x.ExitAt, x.Id });
        b.HasIndex(x => new { x.State, x.NextClosureAt, x.Id });
        b.Property(x => x.State).HasConversion<string>().HasMaxLength(24).IsUnicode(false);
        b.Property(x => x.EntrySource).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        b.Property(x => x.ExitSource).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        b.Property(x => x.Revision).IsConcurrencyToken();
        StayMappingRules.Utc(b, nameof(VehicleStayRow.CreatedAt), nameof(VehicleStayRow.UpdatedAt),
            nameof(VehicleStayRow.EntryAt), nameof(VehicleStayRow.ExitAt), nameof(VehicleStayRow.NextClosureAt));
        StayMappingRules.PartitionContext(b);
        b.HasOne<StayPolicyRow>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Restrict);
        StayMappingRules.EventSupport(b, nameof(VehicleStayRow.OriginEventId));
        StayMappingRules.EventSupport(b, nameof(VehicleStayRow.EntryEventId));
        StayMappingRules.EventSupport(b, nameof(VehicleStayRow.ExitEventId));
        StayMappingRules.Immutable(b, nameof(VehicleStayRow.PartitionId), nameof(VehicleStayRow.PolicyId),
            nameof(VehicleStayRow.OriginEventId), nameof(VehicleStayRow.CreatedAt), nameof(VehicleStayRow.CreationOrder),
            nameof(VehicleStayRow.WorkshopId), nameof(VehicleStayRow.PlateNormalized));
        b.HasOne<VehicleStayRow>().WithMany().HasForeignKey(x => new { x.PartitionId, x.AbsorbedByStayId })
            .HasPrincipalKey(x => new { x.PartitionId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StayProjectionRevisionRow>().WithMany().HasForeignKey(x => new { x.PartitionId, x.Revision }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StayProjectionRevisionMapping : IEntityTypeConfiguration<StayProjectionRevisionRow>
{
    public void Configure(EntityTypeBuilder<StayProjectionRevisionRow> b)
    {
        b.ToTable("StayProjectionRevisions", t =>
        {
            t.HasCheckConstraint("CK_StayRevision_Values", "[Revision] > 0 AND [InputRevision] >= 0 AND [FormatVersion] > 0 AND ISJSON([ChangesJson]) = 1");
            t.HasCheckConstraint("CK_StayRevision_Cause", "([Cause] = 'Ingestion' AND [CausingEventId] IS NOT NULL) OR ([Cause] IN ('DailyClosure','InitialProjection') AND [CausingEventId] IS NULL)");
            t.HasCheckConstraint("CK_StayRevision_Utc", "DATEPART(TZOFFSET,[EvaluatedAt]) = 0");
        });
        b.HasKey(x => new { x.PartitionId, x.Revision });
        b.Property(x => x.Revision).ValueGeneratedNever();
        b.Property(x => x.Cause).HasConversion<string>().HasMaxLength(24).IsUnicode(false);
        b.Property(x => x.ChangesJson).IsRequired();
        StayMappingRules.Utc(b, nameof(StayProjectionRevisionRow.EvaluatedAt));
        StayMappingRules.PartitionContext(b);
        StayMappingRules.EventSupport(b, nameof(StayProjectionRevisionRow.CausingEventId));
    }
}

internal sealed class StayLineageMapping : IEntityTypeConfiguration<StayLineageRow>
{
    public void Configure(EntityTypeBuilder<StayLineageRow> b)
    {
        b.ToTable("StayLineage", t =>
        {
            t.HasCheckConstraint("CK_StayLineage_Kind", "[Kind] IN ('Fusion','Split') AND [SourceStayId] <> [TargetStayId]");
            t.HasCheckConstraint("CK_StayLineage_Utc", "DATEPART(TZOFFSET,[RecordedAt]) = 0");
        });
        b.HasKey(x => new { x.PartitionId, x.Revision, x.Kind, x.SourceStayId, x.TargetStayId });
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(8).IsUnicode(false);
        StayMappingRules.Utc(b, nameof(StayLineageRow.RecordedAt));
        b.HasOne<StayProjectionRevisionRow>().WithMany().HasForeignKey(x => new { x.PartitionId, x.Revision }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VehicleStayRow>().WithMany().HasForeignKey(x => new { x.PartitionId, x.SourceStayId })
            .HasPrincipalKey(x => new { x.PartitionId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<VehicleStayRow>().WithMany().HasForeignKey(x => new { x.PartitionId, x.TargetStayId })
            .HasPrincipalKey(x => new { x.PartitionId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StayObservationAssignmentMapping : IEntityTypeConfiguration<StayObservationAssignmentRow>
{
    public void Configure(EntityTypeBuilder<StayObservationAssignmentRow> b)
    {
        b.ToTable("StayObservationAssignments", t =>
        {
            t.HasCheckConstraint("CK_StayAssignment_Role", "([StayId] IS NULL AND [Role] = 'Unassociated') OR ([StayId] IS NOT NULL AND [Role] IN ('Entry','Exit','Intermediate','Repeated'))");
            t.HasCheckConstraint("CK_StayAssignment_Group", "([GroupId] IS NULL AND [GroupOriginEventId] IS NULL AND [StayId] IS NULL) OR ([GroupId] IS NOT NULL AND [GroupOriginEventId] IS NOT NULL)");
            t.HasCheckConstraint("CK_StayAssignment_Reason", "LEN(LTRIM(RTRIM([Reason]))) > 0");
        });
        b.HasKey(x => x.EventId);
        b.Property(x => x.EventId).ValueGeneratedNever();
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        b.Property(x => x.Reason).HasMaxLength(64).IsUnicode(false).IsRequired();
        b.HasIndex(x => new { x.StayId, x.EventId });
        b.HasIndex(x => new { x.PartitionId, x.EventId });
        b.HasIndex(x => new { x.PartitionId, x.GroupId });
        StayMappingRules.PartitionContext(b);
        StayMappingRules.EventSupport(b, nameof(StayObservationAssignmentRow.EventId));
        StayMappingRules.EventSupport(b, nameof(StayObservationAssignmentRow.GroupOriginEventId));
        StayMappingRules.EventSupport(b, nameof(StayObservationAssignmentRow.MatchedMovementEventId));
        b.HasOne<VehicleStayRow>().WithMany().HasForeignKey(x => new { x.PartitionId, x.StayId })
            .HasPrincipalKey(x => new { x.PartitionId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StayProjectionRevisionRow>().WithMany().HasForeignKey(x => new { x.PartitionId, x.Revision }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StayDayClosureMapping : IEntityTypeConfiguration<StayDayClosureRow>
{
    public void Configure(EntityTypeBuilder<StayDayClosureRow> b)
    {
        b.ToTable("StayDayClosures", t =>
        {
            t.HasCheckConstraint("CK_StayClosure_Revision", "[ProcessedInputRevision] >= 0");
            t.HasCheckConstraint("CK_StayClosure_Utc", "DATEPART(TZOFFSET,[ClosedAt]) = 0 AND ([NextCheckAt] IS NULL OR DATEPART(TZOFFSET,[NextCheckAt]) = 0)");
        });
        b.HasKey(x => new { x.PartitionId, x.LocalDate });
        b.Property(x => x.LocalDate).HasColumnType("date");
        b.Property(x => x.ProcessedInputRevision).IsConcurrencyToken();
        StayMappingRules.Utc(b, nameof(StayDayClosureRow.ClosedAt), nameof(StayDayClosureRow.NextCheckAt));
        b.HasIndex(x => new { x.NextCheckAt, x.LocalDate, x.PartitionId });
        b.HasOne<VehicleStayPartitionRow>().WithMany().HasForeignKey(x => x.PartitionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LprDeviceLocationMapping : IEntityTypeConfiguration<LprDeviceLocationRow>
{
    public void Configure(EntityTypeBuilder<LprDeviceLocationRow> b)
    {
        b.ToTable("LprDeviceLocations", t =>
        {
            t.HasCheckConstraint("CK_DeviceLocation_Identity", $"[Id] <> {StayMappingRules.NonEmptyGuid} AND [CreationOrder] > 0");
            t.HasCheckConstraint("CK_DeviceLocation_Description", "LEN(LTRIM(RTRIM([Description]))) > 0");
            t.HasCheckConstraint("CK_DeviceLocation_Utc", "DATEPART(TZOFFSET,[RecordedAt]) = 0");
        });
        b.HasKey(x => x.Id);
        StayMappingRules.Identity(b);
        b.Property(x => x.WorkshopId).HasMaxLength(256).IsRequired();
        b.Property(x => x.Description).HasMaxLength(256).IsRequired();
        StayMappingRules.Immutable(b, nameof(LprDeviceLocationRow.DeviceId), nameof(LprDeviceLocationRow.WorkshopId),
            nameof(LprDeviceLocationRow.Description), nameof(LprDeviceLocationRow.RecordedAt), nameof(LprDeviceLocationRow.CreationOrder));
        StayMappingRules.Utc(b, nameof(LprDeviceLocationRow.RecordedAt));
        b.HasAlternateKey(x => new { x.Id, x.DeviceId, x.WorkshopId });
        b.HasIndex(x => new { x.DeviceId, x.CreationOrder }).IsUnique();
        b.HasIndex(x => new { x.DeviceId, x.RecordedAt, x.Id });
        b.HasOne<LprDevice>().WithMany().HasForeignKey(x => new { x.DeviceId, x.WorkshopId })
            .HasPrincipalKey(x => new { x.Id, x.WorkshopId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LprObservationLocationMapping : IEntityTypeConfiguration<LprObservationLocationRow>
{
    public void Configure(EntityTypeBuilder<LprObservationLocationRow> b)
    {
        b.ToTable("LprObservationLocations", t => t.HasCheckConstraint("CK_ObservationLocation_Provenance",
            "([LocationId] IS NULL AND [Provenance] = 'Unknown') OR ([LocationId] IS NOT NULL AND [Provenance] IN ('AtCapture','LatestKnownForFutureCapture'))"));
        b.HasKey(x => x.EventId);
        b.Property(x => x.EventId).ValueGeneratedNever();
        b.Property(x => x.WorkshopId).HasMaxLength(256).IsRequired();
        b.Property(x => x.Provenance).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        b.HasOne<LprEventRow>().WithMany().HasForeignKey(x => new { x.EventId, x.DeviceId, x.WorkshopId })
            .HasPrincipalKey(x => new { x.EventId, x.DeviceId, x.WorkshopId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LprDeviceLocationRow>().WithMany().HasForeignKey(x => new { x.LocationId, x.DeviceId, x.WorkshopId })
            .HasPrincipalKey(x => new { x.Id, x.DeviceId, x.WorkshopId }).OnDelete(DeleteBehavior.Restrict);
    }
}
