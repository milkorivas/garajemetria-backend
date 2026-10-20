using GarajeMetria.Application.Authentication;
using GarajeMetria.Domain.Authentication;
using Microsoft.EntityFrameworkCore;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class AuthenticationRepository(AuthenticationDbContext db, TimeProvider clock)
    : IUserAccessQuery, IAuthenticationSessionRepository, ISessionStatusQuery
{
    public async Task<LocalIdentity?> FindAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user is null) return null;
        // An unassigned profile must not require an initial workshop catalog.
        var catalogAvailable = user.DefaultWorkshopId is not null && await db.Catalog.AsNoTracking()
            .AnyAsync(x => x.Id == 1 && x.LastPublishedAt != null, cancellationToken);
        var active = user.DefaultWorkshopId is not null && await db.Workshops.AsNoTracking()
            .AnyAsync(x => x.Id == user.DefaultWorkshopId && x.IsActive, cancellationToken);
        return new(user.Id, user.DirectoryObjectId, user.Username, user.DisplayName,
            user.DefaultWorkshopId, catalogAvailable, active);
    }

    public Task<LocalIdentity> ProvisionAsync(DirectoryIdentity identity, CancellationToken cancellationToken) =>
        SqlTransactions.ExecuteAsync(db, "authentication:directory:" + identity.ObjectId.ToString("N"), async ct =>
        {
            var user = await db.Users.SingleOrDefaultAsync(x => x.DirectoryObjectId == identity.ObjectId, ct);
            if (user is null)
            {
                user = new()
                {
                    Id = Guid.NewGuid(),
                    DirectoryObjectId = identity.ObjectId,
                    Username = identity.Username,
                    DisplayName = identity.DisplayName
                };
                db.Users.Add(user);
                db.ProvisioningAudits.Add(new() { Id = Guid.NewGuid(), UserId = user.Id, OccurredAt = clock.GetUtcNow() });
                await db.SaveChangesAsync(ct);
            }
            else
            {
                user.Username = identity.Username;
                user.DisplayName = identity.DisplayName;
                await db.SaveChangesAsync(ct);
            }
            return (await FindAsync(user.Id, ct))!;
        }, cancellationToken);

    Task<AuthenticationSession?> IAuthenticationSessionRepository.FindAsync(Guid sessionId, CancellationToken cancellationToken) =>
        db.Sessions.SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);

    public Task<AuthenticationSession?> FindOpenAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Sessions.SingleOrDefaultAsync(x => x.UserId == userId && x.ClosedAt == null, cancellationToken);
    public Task<RefreshTokenRecord?> FindRefreshAsync(Guid tokenId, CancellationToken cancellationToken) =>
        db.RefreshTokens.SingleOrDefaultAsync(x => x.Id == tokenId, cancellationToken);
    public void Add(AuthenticationSession session) => db.Sessions.Add(session);
    public void Add(RefreshTokenRecord refresh) => db.RefreshTokens.Add(refresh);

    public async Task<bool> IsActiveAsync(TokenIdentity identity, CancellationToken cancellationToken)
    {
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == identity.SessionId, cancellationToken);
        return session?.Allows(identity.UserId, identity.Role, clock.GetUtcNow()) == true;
    }
}
