using GarajeMetria.Domain.Workshops;

namespace GarajeMetria.Application.Workshops;

public sealed record WorkshopDto(string Id, string Code, string Name, bool IsActive);
public sealed record WorkshopPage(IReadOnlyList<WorkshopDto> Items, int PageNumber, int PageSize, int TotalCount);
public sealed record WorkshopRead(IReadOnlyList<WorkshopEntry> Items, bool IsComplete, bool EmptyConfirmed);
public sealed record CatalogObservation(long Version, DateTimeOffset StartedAt);
public sealed record CatalogPublication(bool Published, DateTimeOffset? LastPublishedAt,
    int Added = 0, int Changed = 0, int Deactivated = 0, int Reactivated = 0);

public interface IWorkshopSource
{
    Task<WorkshopRead> ReadAsync(CancellationToken cancellationToken);
}

public interface IWorkshopCatalogStore
{
    Task<CatalogObservation> BeginObservationAsync(CancellationToken cancellationToken);
    Task<CatalogPublication> PublishAsync(CatalogObservation observation, IReadOnlyList<WorkshopEntry> entries,
        CancellationToken cancellationToken);
    Task<DateTimeOffset?> LastPublishedAsync(CancellationToken cancellationToken);
    Task<WorkshopPage?> ListAsync(int pageNumber, int pageSize, bool? isActive, CancellationToken cancellationToken);
}

public sealed class WorkshopCatalogOptions
{
    public bool Enabled { get; set; }
    public string Source { get; set; } = "Disabled";
    public int IntervalSeconds { get; set; } = 300;
    public int TimeoutSeconds { get; set; } = 60;
    public int DefaultPageSize { get; set; } = 20;
    public int MaxPageSize { get; set; } = 100;
}
