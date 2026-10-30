using GarajeMetria.Application.Users;
using GarajeMetria.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class UserAdministrationRepository(AuthenticationDbContext db) : IUserAdministrationQuery,
    ILocalUserRepository, IWorkshopQuery, IWorkshopAssignmentAuditRepository
{
    public async Task<LocalUserPage> ListAsync(int pageNumber, int pageSize, CancellationToken cancellationToken)
    {
        var count = await db.Users.CountAsync(cancellationToken);
        var offset = ((long)pageNumber - 1) * pageSize;
        if (offset >= count) return new([], pageNumber, pageSize, count);
        var items = await db.Users.AsNoTracking().OrderBy(x => x.Id).Skip((int)offset).Take(pageSize)
            .Select(x => new LocalUserDto(x.Id.ToString(), x.DisplayName, x.Username, x.DefaultWorkshopId))
            .ToArrayAsync(cancellationToken);
        return new(items, pageNumber, pageSize, count);
    }

    public async Task<AssignmentState?> FindAsync(string userId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(userId, out var id)) return null;
        return await db.Users.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new AssignmentState(x.Id, x.DirectoryObjectId, x.DefaultWorkshopId, x.AssignmentVersion))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> SetAssignmentAsync(Guid userId, Guid expectedVersion, string? workshopId, Guid version,
        CancellationToken cancellationToken)
    {
        // This contract participates in the caller's assignment/audit/session transaction.
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("An assignment transaction is required.");
        return await db.Users.Where(x => x.Id == userId && x.AssignmentVersion == expectedVersion)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.DefaultWorkshopId, workshopId)
                .SetProperty(x => x.AssignmentVersion, version), cancellationToken) == 1;
    }

    public async Task<WorkshopAvailability> CheckAsync(string workshopId, CancellationToken cancellationToken)
    {
        if (!await db.Catalog.AsNoTracking().AnyAsync(x => x.Id == 1 && x.LastPublishedAt != null, cancellationToken))
            return WorkshopAvailability.CatalogUnavailable;
        var workshop = await db.Workshops.AsNoTracking().Where(x => x.Id == workshopId)
            .Select(x => new { x.Id, x.IsActive }).SingleOrDefaultAsync(cancellationToken);
        // Opaque identifiers use ordinal equality, independently of the database collation.
        if (workshop is null || !string.Equals(workshop.Id, workshopId, StringComparison.Ordinal)) return WorkshopAvailability.Missing;
        return workshop.IsActive ? WorkshopAvailability.Active : WorkshopAvailability.Inactive;
    }

    public void Add(WorkshopAssignmentAuditEntry entry) => db.AssignmentAudits.Add(entry);
}

internal sealed class WorkshopAssignmentAuditMapping : IEntityTypeConfiguration<WorkshopAssignmentAuditEntry>
{
    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<WorkshopAssignmentAuditEntry> b)
    {
        b.ToTable("WorkshopAssignmentAudits");
        b.HasKey(x => x.Id);
        b.Property(x => x.PreviousWorkshopId).HasMaxLength(256);
        b.Property(x => x.NewWorkshopId).HasMaxLength(256);
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.CorrelationId).HasMaxLength(256).IsRequired();
        b.Property(x => x.OccurredAt).HasPrecision(7);
        b.HasIndex(x => x.OccurredAt);
        b.HasIndex(x => new { x.UserId, x.OccurredAt });
        b.HasOne<LocalUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LocalUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkshopAccess>().WithMany().HasForeignKey(x => x.PreviousWorkshopId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkshopAccess>().WithMany().HasForeignKey(x => x.NewWorkshopId).OnDelete(DeleteBehavior.Restrict);
    }
}
