using System.Diagnostics;
using GarajeMetria.Application.Users;

namespace GarajeMetria.Api.Users;

public sealed class HttpCurrentUserContext(IHttpContextAccessor accessor) : ICurrentUserContext
{
    public CurrentUser? User
    {
        get
        {
            var principal = accessor.HttpContext?.User;
            return principal?.Identity?.IsAuthenticated == true && Guid.TryParse(principal.FindFirst("sub")?.Value, out var id) &&
                Guid.TryParse(principal.FindFirst("sid")?.Value, out var session) && principal.FindFirst("role")?.Value is { } role
                ? new(id, session, role) : null;
        }
    }

    public string CorrelationId => Activity.Current?.TraceId.ToString() ?? accessor.HttpContext?.TraceIdentifier ?? Guid.NewGuid().ToString("N");
}
