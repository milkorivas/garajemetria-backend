using MediatR;

namespace GarajeMetria.Application.Authentication;

// Resolve SQL-backed session checks only after the transport has a valid access identity.
public sealed record SessionActiveQuery(TokenIdentity Identity) : IRequest<bool>
{
    public override string ToString() => nameof(SessionActiveQuery);
}

public sealed class SessionActiveQueryHandler(ISessionStatusQuery sessions) : IRequestHandler<SessionActiveQuery, bool>
{
    public Task<bool> Handle(SessionActiveQuery request, CancellationToken cancellationToken) =>
        sessions.IsActiveAsync(request.Identity, cancellationToken);
}
