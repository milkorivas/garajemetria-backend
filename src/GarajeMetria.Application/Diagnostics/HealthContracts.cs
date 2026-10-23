using MediatR;

namespace GarajeMetria.Application.Diagnostics;

public enum ModuleStatus { Ok, Error, NotConfigured }

public interface IHealthDiagnostics
{
    Task<ModuleStatus> CheckSqlAsync(CancellationToken cancellationToken);
    Task<ModuleStatus> CheckActiveDirectoryAsync(CancellationToken cancellationToken);
}

public interface IAuthenticationAvailability
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
}

public sealed record HealthQuery : IRequest<HealthResult>;
public sealed record HealthResult(ModuleStatus Sql, ModuleStatus ActiveDirectory)
{
    public bool Healthy => Sql == ModuleStatus.Ok && ActiveDirectory == ModuleStatus.Ok;
}

public sealed class HealthQueryHandler(IHealthDiagnostics diagnostics) : IRequestHandler<HealthQuery, HealthResult>
{
    public async Task<HealthResult> Handle(HealthQuery request, CancellationToken cancellationToken)
    {
        var sql = diagnostics.CheckSqlAsync(cancellationToken);
        var directory = diagnostics.CheckActiveDirectoryAsync(cancellationToken);
        await Task.WhenAll(sql, directory);
        return new(await sql, await directory);
    }
}
