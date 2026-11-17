using GarajeMetria.Domain.Lpr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenIddict.EntityFrameworkCore.Models;

namespace GarajeMetria.Infrastructure.Persistence;

// Persistence rows preserve the original classifier version without invoking today's classifier.
public sealed class LprEventRow
{
    public Guid EventId { get; set; }
    public Guid DeviceId { get; set; }
    public string WorkshopId { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string PlateOriginal { get; set; } = "";
    public string PlateNormalized { get; set; } = "";
    public string PlateStatus { get; set; } = "";
    public string? PlateFormat { get; set; }
    public string PlateRulesVersion { get; set; } = "";
    public decimal Confidence { get; set; }
    public LprDirection? Direction { get; set; }
    public byte[] EventOrderKey { get; private set; } = [];
    public override string ToString() => nameof(LprEventRow);
}

public sealed class DeviceAdministrationAuditRow
{
    public Guid Id { get; set; }
    public Guid ActorId { get; set; }
    public Guid DeviceId { get; set; }
    public DeviceAdministrationOperation Operation { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string CorrelationId { get; set; } = "";
    public DeviceAdministrationSnapshot? Previous { get; set; }
    public DeviceAdministrationSnapshot Current { get; set; } = null!;
}

internal sealed class LprDeviceMapping : IEntityTypeConfiguration<LprDevice>
{
    public void Configure(EntityTypeBuilder<LprDevice> b)
    {
        b.ToTable("LprDevices", t =>
        {
            t.HasCheckConstraint("CK_LprDevice_State", "[IsActive] = 1 OR [HasCredentials] = 0");
            t.HasCheckConstraint("CK_LprDevice_Identity", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [Version] <> '00000000-0000-0000-0000-000000000000' AND [AuthorizationVersion] <> '00000000-0000-0000-0000-000000000000'");
            t.HasCheckConstraint("CK_LprDevice_Name", "LEN(LTRIM(RTRIM([Name]))) > 0");
            t.HasCheckConstraint("CK_LprDevice_Utc", "DATEPART(TZOFFSET,[CreatedAt]) = 0 AND DATEPART(TZOFFSET,[UpdatedAt]) = 0");
        });
        b.HasKey(x => x.Id);
        b.Ignore(x => x.Location);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.WorkshopId).HasMaxLength(256).IsRequired();
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.CreatedAt).HasPrecision(7);
        b.Property(x => x.UpdatedAt).HasPrecision(7);
        b.HasAlternateKey(x => new { x.Id, x.WorkshopId });
        b.HasIndex(x => new { x.WorkshopId, x.IsActive });
        b.Property<string>("OAuthApplicationId").HasMaxLength(450).IsRequired();
        b.HasIndex("OAuthApplicationId").IsUnique();
        b.HasOne<OpenIddictEntityFrameworkCoreApplication>().WithOne().HasForeignKey<LprDevice>("OAuthApplicationId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkshopAccess>().WithMany().HasForeignKey(x => x.WorkshopId).OnDelete(DeleteBehavior.Restrict);
        b.Property<Guid?>("CurrentLocationId");
        b.HasOne<LprDeviceLocationRow>().WithMany().HasForeignKey("CurrentLocationId", "Id", "WorkshopId")
            .HasPrincipalKey(x => new { x.Id, x.DeviceId, x.WorkshopId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LprEventMapping : IEntityTypeConfiguration<LprEventRow>
{
    public void Configure(EntityTypeBuilder<LprEventRow> b)
    {
        b.ToTable("LprEvents", t =>
        {
            t.HasCheckConstraint("CK_LprEvent_Confidence", "[Confidence] >= 0 AND [Confidence] <= 1");
            t.HasCheckConstraint("CK_LprEvent_Direction", "[Direction] IS NULL OR [Direction] IN ('entry','exit')");
            t.HasCheckConstraint("CK_LprEvent_Plate", "LEN([PlateOriginal]) > 0 AND LEN([PlateNormalized]) > 0 AND LEN([PlateRulesVersion]) > 0 AND (([PlateStatus] = 'valid' AND [PlateFormat] IS NOT NULL AND LEN([PlateFormat]) > 0) OR ([PlateStatus] = 'invalid' AND [PlateFormat] IS NULL))");
            t.HasCheckConstraint("CK_LprEvent_Utc", "DATEPART(TZOFFSET,[CapturedAt]) = 0 AND DATEPART(TZOFFSET,[ReceivedAt]) = 0");
            t.HasCheckConstraint("CK_LprEvent_Uuid", "SUBSTRING(CONVERT(varchar(36),[EventId]),15,1) IN ('1','2','3','4','5','6','7','8') AND SUBSTRING(CONVERT(varchar(36),[EventId]),20,1) IN ('8','9','a','b','A','B')");
        });
        b.HasKey(x => x.EventId);
        b.Property(x => x.EventId).ValueGeneratedNever();
        b.HasAlternateKey(x => new { x.EventId, x.DeviceId, x.WorkshopId });
        b.HasAlternateKey(x => new { x.EventId, x.WorkshopId, x.PlateNormalized });
        // Text UUID hex is in network order; SQL Server's native uniqueidentifier binary/sort order is not.
        b.Property(x => x.EventOrderKey).HasColumnType("binary(16)")
            .HasComputedColumnSql("CONVERT(binary(16), REPLACE(CONVERT(char(36), [EventId]), '-', ''), 2)", stored: true);
        b.Property(x => x.WorkshopId).HasMaxLength(256).IsRequired();
        b.Property(x => x.CapturedAt).HasPrecision(7);
        b.Property(x => x.ReceivedAt).HasPrecision(7);
        b.Property(x => x.PlateOriginal).HasMaxLength(32).IsRequired();
        b.Property(x => x.PlateNormalized).HasMaxLength(32).IsRequired();
        b.Property(x => x.PlateStatus).HasMaxLength(16).IsUnicode(false).IsRequired();
        b.Property(x => x.PlateFormat).HasMaxLength(64).IsUnicode(false);
        b.Property(x => x.PlateRulesVersion).HasMaxLength(32).IsUnicode(false).IsRequired();
        b.Property(x => x.Confidence).HasPrecision(9, 8);
        b.Property(x => x.Direction).HasMaxLength(5).IsUnicode(false).HasConversion(
            x => x == LprDirection.Entry ? "entry" : "exit", x => x == "entry" ? LprDirection.Entry : LprDirection.Exit);
        b.HasIndex(x => new { x.WorkshopId, x.CapturedAt, x.EventId });
        b.HasIndex(x => new { x.DeviceId, x.CapturedAt, x.EventId });
        b.HasIndex(x => new { x.WorkshopId, x.PlateNormalized, x.CapturedAt, x.EventOrderKey });
        b.HasOne<LprDevice>().WithMany().HasForeignKey(x => new { x.DeviceId, x.WorkshopId })
            .HasPrincipalKey(x => new { x.Id, x.WorkshopId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkshopAccess>().WithMany().HasForeignKey(x => x.WorkshopId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DeviceAdministrationAuditMapping : IEntityTypeConfiguration<DeviceAdministrationAuditRow>
{
    public void Configure(EntityTypeBuilder<DeviceAdministrationAuditRow> b)
    {
        b.ToTable("DeviceAdministrationAudits", t =>
        {
            t.HasCheckConstraint("CK_DeviceAudit_Operation", "[Operation] IN ('Created','Renamed','Deactivated','Reactivated','CredentialsRotated','CredentialsRevoked','LocationChanged')");
            t.HasCheckConstraint("CK_DeviceAudit_Location", "([CurrentLocationId] IS NULL AND [CurrentLocationDescription] IS NULL AND [CurrentLocationRecordedAt] IS NULL OR [CurrentLocationId] IS NOT NULL AND [CurrentLocationDescription] IS NOT NULL AND LEN(LTRIM(RTRIM([CurrentLocationDescription]))) > 0 AND [CurrentLocationRecordedAt] IS NOT NULL AND DATEPART(TZOFFSET,[CurrentLocationRecordedAt]) = 0) AND ([PreviousLocationId] IS NULL AND [PreviousLocationDescription] IS NULL AND [PreviousLocationRecordedAt] IS NULL OR [PreviousLocationId] IS NOT NULL AND [PreviousLocationDescription] IS NOT NULL AND LEN(LTRIM(RTRIM([PreviousLocationDescription]))) > 0 AND [PreviousLocationRecordedAt] IS NOT NULL AND DATEPART(TZOFFSET,[PreviousLocationRecordedAt]) = 0)");
            t.HasCheckConstraint("CK_DeviceAudit_LocationChanged", "[Operation] <> 'LocationChanged' OR ([CurrentLocationId] IS NOT NULL AND ([PreviousLocationId] IS NULL OR [CurrentLocationId] <> [PreviousLocationId]) AND [CurrentAuthorizationVersion] = [PreviousAuthorizationVersion] AND [CurrentName] = [PreviousName] AND [CurrentIsActive] = [PreviousIsActive] AND [CurrentHasCredentials] = [PreviousHasCredentials])");
            t.HasCheckConstraint("CK_DeviceAudit_Snapshot", "[CurrentDeviceId] = [DeviceId] AND [CurrentUpdatedAt] = [OccurredAt] AND (([Operation] = 'Created' AND [PreviousDeviceId] IS NULL) OR ([Operation] <> 'Created' AND [PreviousDeviceId] IS NOT NULL AND [PreviousVersion] IS NOT NULL AND [PreviousAuthorizationVersion] IS NOT NULL AND [PreviousWorkshopId] IS NOT NULL AND [PreviousName] IS NOT NULL AND [PreviousIsActive] IS NOT NULL AND [PreviousHasCredentials] IS NOT NULL AND [PreviousCreatedAt] IS NOT NULL AND [PreviousUpdatedAt] IS NOT NULL AND [PreviousDeviceId] = [DeviceId] AND [PreviousVersion] <> [CurrentVersion] AND [PreviousWorkshopId] = [CurrentWorkshopId]))");
            t.HasCheckConstraint("CK_DeviceAudit_Utc", "DATEPART(TZOFFSET,[OccurredAt]) = 0 AND DATEPART(TZOFFSET,[CurrentCreatedAt]) = 0 AND DATEPART(TZOFFSET,[CurrentUpdatedAt]) = 0 AND ([PreviousDeviceId] IS NULL OR (DATEPART(TZOFFSET,[PreviousCreatedAt]) = 0 AND DATEPART(TZOFFSET,[PreviousUpdatedAt]) = 0))");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Operation).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        b.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
        b.Property(x => x.OccurredAt).HasPrecision(7);
        b.HasIndex(x => new { x.OccurredAt, x.Id });
        b.HasIndex(x => new { x.DeviceId, x.OccurredAt });
        b.HasOne<LocalUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LprDevice>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.OwnsOne(x => x.Previous, owned => Snapshot(owned, "Previous"));
        b.OwnsOne(x => x.Current, owned => Snapshot(owned, "Current"));
        b.Navigation(x => x.Current).IsRequired();
    }

    private static void Snapshot(OwnedNavigationBuilder<DeviceAdministrationAuditRow, DeviceAdministrationSnapshot> b, string prefix)
    {
        b.Property(x => x.DeviceId).HasColumnName(prefix + "DeviceId");
        b.Property(x => x.WorkshopId).HasColumnName(prefix + "WorkshopId").HasMaxLength(256).IsRequired();
        b.Property(x => x.Name).HasColumnName(prefix + "Name").HasMaxLength(128).IsRequired();
        b.Property(x => x.IsActive).HasColumnName(prefix + "IsActive");
        b.Property(x => x.HasCredentials).HasColumnName(prefix + "HasCredentials");
        b.Property(x => x.Version).HasColumnName(prefix + "Version");
        b.Property(x => x.AuthorizationVersion).HasColumnName(prefix + "AuthorizationVersion");
        b.Property(x => x.CreatedAt).HasColumnName(prefix + "CreatedAt").HasPrecision(7);
        b.Property(x => x.UpdatedAt).HasColumnName(prefix + "UpdatedAt").HasPrecision(7);
        b.Property(x => x.LocationId).HasColumnName(prefix + "LocationId");
        b.Property(x => x.LocationDescription).HasColumnName(prefix + "LocationDescription").HasMaxLength(256);
        b.Property(x => x.LocationRecordedAt).HasColumnName(prefix + "LocationRecordedAt").HasPrecision(7);
    }
}
