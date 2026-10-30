using System.Data;
using GarajeMetria.Application.Workshops;
using GarajeMetria.Domain.Workshops;
using Microsoft.EntityFrameworkCore;

namespace GarajeMetria.Infrastructure.Workshops;

internal sealed class DisabledWorkshopSource : IWorkshopSource
{
    public Task<WorkshopRead> ReadAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Workshop synchronization is disabled.");
}

public sealed class WorkshopSourceOptions
{
    public int CommandTimeoutSeconds { get; set; } = 30;
}

internal sealed class ExternalWorkshop
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}

internal sealed class WorkshopSourceDbContext(DbContextOptions<WorkshopSourceDbContext> options) : DbContext(options)
{
    public DbSet<ExternalWorkshop> Workshops => Set<ExternalWorkshop>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var workshop = modelBuilder.Entity<ExternalWorkshop>();
        workshop.HasNoKey().ToView("ActiveWorkshops", "dbo");
        workshop.Property(x => x.Code).HasMaxLength(12).IsRequired();
        workshop.Property(x => x.Name).HasMaxLength(256).IsRequired();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("The external workshop source is read-only.");

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The external workshop source is read-only.");
}

internal sealed class SqlWorkshopSource(WorkshopSourceDbContext db) : IWorkshopSource
{
    public async Task<WorkshopRead> ReadAsync(CancellationToken cancellationToken)
    {
        // A single serializable read is a coherent snapshot, including a confirmed empty result.
        // This external transaction ends before the local catalog publication begins.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var entries = await db.Workshops.AsNoTracking().OrderBy(x => x.Code)
            .Select(x => new WorkshopEntry(x.Code, x.Name)).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(entries, true, true);
    }
}
