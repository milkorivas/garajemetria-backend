using GarajeMetria.Domain.Users;

namespace GarajeMetria.Application.Users;

public sealed record CurrentUser(Guid Id, Guid SessionId, string Role);
public interface ICurrentUserContext
{
    CurrentUser? User { get; }
    string CorrelationId { get; }
}

public sealed record LocalUserDto(string Id, string DisplayName, string Username, string? DefaultWorkshopId);
public sealed record LocalUserPage(IReadOnlyList<LocalUserDto> Items, int PageNumber, int PageSize, int TotalCount);
public sealed record AssignmentState(Guid UserId, Guid DirectoryObjectId, string? WorkshopId, Guid Version);
public sealed record AssignmentResult(string UserId, string? WorkshopId, Guid Version);

public interface IUserAdministrationQuery
{
    Task<LocalUserPage> ListAsync(int pageNumber, int pageSize, CancellationToken cancellationToken);
    Task<AssignmentState?> FindAsync(string userId, CancellationToken cancellationToken);
}

public interface ILocalUserRepository
{
    Task<bool> SetAssignmentAsync(Guid userId, Guid expectedVersion, string? workshopId, Guid version, CancellationToken cancellationToken);
}

public enum WorkshopAvailability { CatalogUnavailable, Missing, Inactive, Active }
public interface IWorkshopQuery
{
    Task<WorkshopAvailability> CheckAsync(string workshopId, CancellationToken cancellationToken);
}

public interface IWorkshopAssignmentAuditRepository
{
    void Add(WorkshopAssignmentAuditEntry entry);
}

public sealed class UserAdministrationOptions
{
    public int DefaultPageSize { get; set; } = 20;
    public int MaxPageSize { get; set; } = 100;
    public int MaxBodyBytes { get; set; } = 16 * 1024;
}
