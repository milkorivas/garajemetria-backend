using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GarajeMetria.Infrastructure.Persistence;

public sealed class HistoryAccessAuditRow
{
    public Guid Id { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Operation { get; set; } = "";
    public string ScopeKind { get; set; } = "";
    public string Status { get; set; } = "AuthorizedRelease";
    public string CorrelationId { get; set; } = "";
    public long RowCount { get; set; }
    public override string ToString() => nameof(HistoryAccessAuditRow);
}
public sealed class HistoryAccessAuditWorkshopRow
{
    public Guid AuditId { get; set; }
    public string WorkshopId { get; set; } = "";
    public override string ToString() => nameof(HistoryAccessAuditWorkshopRow);
}
public sealed class HistoryExportLeaseRow
{
    public Guid UserId { get; set; }
    public Guid Owner { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
internal sealed class HistoryAuditMapping : IEntityTypeConfiguration<HistoryAccessAuditRow>
{
    public void Configure(EntityTypeBuilder<HistoryAccessAuditRow> b)
    {
        b.ToTable("HistoryAccessAudits", t =>
        {
            t.HasCheckConstraint("CK_HistoryAudit_Rows", "[RowCount] >= 0");
            t.HasCheckConstraint("CK_HistoryAudit_Scope", "[ScopeKind] IN ('Selected', 'AllAuthorized')");
            t.HasCheckConstraint("CK_HistoryAudit_Status", "[Status] = 'AuthorizedRelease'");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Operation).HasMaxLength(64).IsUnicode(false).IsRequired();
        b.Property(x => x.ScopeKind).HasMaxLength(16).IsUnicode(false).IsRequired();
        b.Property(x => x.Status).HasMaxLength(24).IsUnicode(false).IsRequired();
        b.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        b.Property(x => x.OccurredAt).HasPrecision(7);
        b.HasIndex(x => new { x.OccurredAt, x.Id });
        b.HasIndex(x => new { x.ActorUserId, x.OccurredAt, x.Id });
        b.HasOne<LocalUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class HistoryAuditWorkshopMapping : IEntityTypeConfiguration<HistoryAccessAuditWorkshopRow>
{
    public void Configure(EntityTypeBuilder<HistoryAccessAuditWorkshopRow> b)
    {
        b.ToTable("HistoryAccessAuditWorkshops");
        b.HasKey(x => new { x.AuditId, x.WorkshopId });
        b.Property(x => x.WorkshopId).HasMaxLength(256).IsRequired();
        b.HasOne<HistoryAccessAuditRow>().WithMany().HasForeignKey(x => x.AuditId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkshopAccess>().WithMany().HasForeignKey(x => x.WorkshopId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class HistoryLeaseMapping : IEntityTypeConfiguration<HistoryExportLeaseRow>
{
    public void Configure(EntityTypeBuilder<HistoryExportLeaseRow> b)
    {
        b.ToTable("HistoryExportLeases");
        b.HasKey(x => x.UserId);
        b.Property(x => x.UserId).ValueGeneratedNever();
        b.Property(x => x.ExpiresAt).HasPrecision(7);
        b.HasIndex(x => x.ExpiresAt);
    }
}
// Additional indexes are provider-owned and additive; existing mappings remain authoritative.
internal sealed class HistoryEventIndexMapping : IEntityTypeConfiguration<LprEventRow>
{
    public void Configure(EntityTypeBuilder<LprEventRow> b) =>
        b.HasIndex(x => new { x.WorkshopId, x.ReceivedAt, x.EventOrderKey });
}
internal sealed class HistoryStayIndexMapping : IEntityTypeConfiguration<VehicleStayRow>
{
    public void Configure(EntityTypeBuilder<VehicleStayRow> b)
    {
        b.Property<byte[]>("StayOrderKey").HasColumnType("binary(16)")
            .HasComputedColumnSql("CONVERT(binary(16), REPLACE(CONVERT(char(36), [Id]), '-', ''), 2)", stored: true);
        b.HasIndex("WorkshopId", "State", "EntryAt", "StayOrderKey").HasFilter("[AbsorbedByStayId] IS NULL");
        b.HasIndex("WorkshopId", "ExitAt", "StayOrderKey").HasFilter("[AbsorbedByStayId] IS NULL");
    }
}
