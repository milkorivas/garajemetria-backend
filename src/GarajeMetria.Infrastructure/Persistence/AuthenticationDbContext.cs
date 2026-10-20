using GarajeMetria.Domain.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GarajeMetria.Infrastructure.Persistence;

public sealed class LocalUser
{
    public Guid Id { get; set; }
    public Guid DirectoryObjectId { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? DefaultWorkshopId { get; set; }
}

public sealed class WorkshopAccess
{
    public string Id { get; set; } = "";
    public bool IsActive { get; set; }
}

public sealed class CatalogState
{
    public int Id { get; set; }
    public DateTimeOffset? LastPublishedAt { get; set; }
}

public sealed class ProvisioningAudit
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class RatePartition
{
    public string Id { get; set; } = "";
}

public sealed class RatePermit
{
    public Guid Id { get; set; }
    public string PartitionId { get; set; } = "";
    public DateTimeOffset AcceptedAt { get; set; }
}

public sealed class RateConfiguration
{
    public string Id { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}

public sealed class CleanupSchedule
{
    public int Id { get; set; }
    public string? Owner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
}

public sealed class AuthenticationDbContext(DbContextOptions<AuthenticationDbContext> options) : DbContext(options)
{
    public DbSet<LocalUser> Users => Set<LocalUser>();
    public DbSet<WorkshopAccess> Workshops => Set<WorkshopAccess>();
    public DbSet<CatalogState> Catalog => Set<CatalogState>();
    public DbSet<AuthenticationSession> Sessions => Set<AuthenticationSession>();
    public DbSet<RefreshTokenRecord> RefreshTokens => Set<RefreshTokenRecord>();
    public DbSet<ProvisioningAudit> ProvisioningAudits => Set<ProvisioningAudit>();
    public DbSet<RatePartition> RatePartitions => Set<RatePartition>();
    public DbSet<RatePermit> RatePermits => Set<RatePermit>();
    public DbSet<RateConfiguration> RateConfigurations => Set<RateConfiguration>();
    public DbSet<CleanupSchedule> CleanupSchedules => Set<CleanupSchedule>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuthenticationDbContext).Assembly);
}

internal sealed class LocalUserMapping : IEntityTypeConfiguration<LocalUser>
{
    public void Configure(EntityTypeBuilder<LocalUser> b)
    {
        b.ToTable("LocalUsers");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.DirectoryObjectId).IsUnique();
        b.Property(x => x.Username).HasMaxLength(512).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(2048).IsRequired();
        b.Property(x => x.DefaultWorkshopId).HasMaxLength(256);
        b.HasOne<WorkshopAccess>().WithMany().HasForeignKey(x => x.DefaultWorkshopId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkshopMapping : IEntityTypeConfiguration<WorkshopAccess>
{
    public void Configure(EntityTypeBuilder<WorkshopAccess> b)
    {
        b.ToTable("WorkshopAccess");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(256);
    }
}

internal sealed class CatalogMapping : IEntityTypeConfiguration<CatalogState>
{
    public void Configure(EntityTypeBuilder<CatalogState> b)
    {
        b.ToTable("WorkshopCatalogState", t => t.HasCheckConstraint("CK_WorkshopCatalogState_Singleton", "[Id] = 1"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.LastPublishedAt).HasPrecision(7);
    }
}

internal sealed class SessionMapping : IEntityTypeConfiguration<AuthenticationSession>
{
    public void Configure(EntityTypeBuilder<AuthenticationSession> b)
    {
        b.ToTable("AuthenticationSessions", t => t.HasCheckConstraint("CK_Session_Lifetime", "[AbsoluteExpiresAt] > [StartedAt]"));
        b.HasKey(x => x.Id);
        b.Ignore(x => x.EndedAt);
        b.Property(x => x.Role).HasMaxLength(32).IsRequired();
        b.Property(x => x.CloseReason).HasMaxLength(64);
        b.Property(x => x.StartedAt).HasPrecision(7);
        b.Property(x => x.AbsoluteExpiresAt).HasPrecision(7);
        b.Property(x => x.ClosedAt).HasPrecision(7);
        b.HasIndex(x => x.UserId).IsUnique().HasFilter("[ClosedAt] IS NULL");
        b.HasIndex(x => x.AbsoluteExpiresAt);
        b.HasIndex(x => x.ClosedAt);
        b.HasOne<LocalUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RefreshMapping : IEntityTypeConfiguration<RefreshTokenRecord>
{
    public void Configure(EntityTypeBuilder<RefreshTokenRecord> b)
    {
        b.ToTable("RefreshTokens", t => t.HasCheckConstraint("CK_Refresh_Lifetime", "[ExpiresAt] > [IssuedAt]"));
        b.HasKey(x => x.Id);
        b.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false).IsRequired();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.Property(x => x.IssuedAt).HasPrecision(0);
        b.Property(x => x.ExpiresAt).HasPrecision(0);
        b.Property(x => x.ConsumedAt).HasPrecision(7).IsConcurrencyToken();
        b.Property(x => x.RevokedAt).HasPrecision(7);
        b.HasOne<AuthenticationSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditMapping : IEntityTypeConfiguration<ProvisioningAudit>
{
    public void Configure(EntityTypeBuilder<ProvisioningAudit> b)
    {
        b.ToTable("ProvisioningAudit");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.UserId).IsUnique();
        b.HasIndex(x => x.OccurredAt);
        b.Property(x => x.OccurredAt).HasPrecision(7);
        b.HasOne<LocalUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RatePartitionMapping : IEntityTypeConfiguration<RatePartition>
{
    public void Configure(EntityTypeBuilder<RatePartition> b)
    {
        b.ToTable("AuthenticationRatePartitions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(64).IsUnicode(false);
    }
}
internal sealed class RatePermitMapping : IEntityTypeConfiguration<RatePermit>
{
    public void Configure(EntityTypeBuilder<RatePermit> b)
    {
        b.ToTable("AuthenticationRatePermits");
        b.HasKey(x => x.Id);
        b.Property(x => x.PartitionId).HasMaxLength(64).IsUnicode(false);
        b.Property(x => x.AcceptedAt).HasPrecision(7);
        b.HasIndex(x => new { x.PartitionId, x.AcceptedAt });
        b.HasOne<RatePartition>().WithMany().HasForeignKey(x => x.PartitionId).OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class RateConfigurationMapping : IEntityTypeConfiguration<RateConfiguration>
{
    public void Configure(EntityTypeBuilder<RateConfiguration> b)
    {
        b.ToTable("AuthenticationRateConfiguration");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(128);
        b.Property(x => x.Fingerprint).HasMaxLength(64).IsUnicode(false).IsRequired();
    }
}
internal sealed class CleanupScheduleMapping : IEntityTypeConfiguration<CleanupSchedule>
{
    public void Configure(EntityTypeBuilder<CleanupSchedule> b)
    {
        b.ToTable("AuthenticationCleanup", t => t.HasCheckConstraint("CK_AuthenticationCleanup_Singleton", "[Id] = 1"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Owner).HasMaxLength(64);
        b.Property(x => x.LeaseUntil).HasPrecision(7);
        b.Property(x => x.NextRunAt).HasPrecision(7);
    }
}
