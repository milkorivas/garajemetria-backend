using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Diagnostics;
using GarajeMetria.Infrastructure.Authentication;
using GarajeMetria.Infrastructure.Diagnostics;
using GarajeMetria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GarajeMetria.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var directory = configuration.GetRequiredSection("Directory").Get<DirectoryOptions>() ?? new();
        var jwt = configuration.GetRequiredSection("Jwt").Get<JwtOptions>() ?? new();
        if (directory.Servers.Length == 0 || directory.Servers.Any(string.IsNullOrWhiteSpace) || directory.Port != 636 ||
            string.IsNullOrWhiteSpace(directory.SearchBase) || string.IsNullOrWhiteSpace(directory.ServiceBindDn) ||
            string.IsNullOrEmpty(directory.ServicePassword) || directory.TimeoutSeconds is <= 0 or > 120 ||
            string.IsNullOrWhiteSpace(directory.AdministratorGroupDn) || string.IsNullOrWhiteSpace(directory.AdvisorGroupDn))
            throw new InvalidOperationException("Invalid or missing Directory configuration.");
        if (string.IsNullOrWhiteSpace(jwt.Issuer) || string.IsNullOrWhiteSpace(jwt.AccessAudience) ||
            string.IsNullOrWhiteSpace(jwt.RefreshAudience) || jwt.AccessAudience == jwt.RefreshAudience || jwt.ClockSkewSeconds != 0 ||
            string.IsNullOrWhiteSpace(jwt.SigningCertificatePath) || string.IsNullOrWhiteSpace(jwt.SigningKeyId))
            throw new InvalidOperationException("Invalid or missing Jwt configuration.");
        services.AddSingleton(directory);
        services.AddSingleton(jwt);
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<AuthenticationDbContext>(o =>
        {
            try
            {
                var connection = configuration.GetConnectionString("Authentication");
                if (string.IsNullOrWhiteSpace(connection)) throw new PersistenceFailureException(FailureCode.ServiceUnavailable);
                o.UseSqlServer(connection, sql => sql.CommandTimeout(15))
                    .AddInterceptors(new PersistenceErrorInterceptor(), new PersistenceConnectionInterceptor());
            }
            catch (ArgumentException ex) { throw new PersistenceFailureException(FailureCode.ServiceUnavailable, ex); }
        });
        services.AddSingleton<AuthenticationAvailability>();
        services.AddSingleton<IAuthenticationAvailability>(sp => sp.GetRequiredService<AuthenticationAvailability>());
        services.AddScoped<IHealthDiagnostics, HealthDiagnostics>();
        services.AddScoped<AuthenticationRepository>();
        services.AddScoped<IUserAccessQuery>(sp => sp.GetRequiredService<AuthenticationRepository>());
        services.AddScoped<IAuthenticationSessionRepository>(sp => sp.GetRequiredService<AuthenticationRepository>());
        services.AddScoped<ISessionStatusQuery>(sp => sp.GetRequiredService<AuthenticationRepository>());
        services.AddScoped<IUnitOfWork, AuthenticationUnitOfWork>();
        services.AddScoped<RateLimitStore>();
        services.AddScoped<IAuthenticationRateLimitStore>(sp => sp.GetRequiredService<RateLimitStore>());
        services.AddScoped<IAuthenticationRetentionStore, RetentionStore>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<ICorporateDirectory, LdapCorporateDirectory>();
        services.AddHostedService<SharedPolicyStartup>();
        services.AddHostedService<AuthenticationCleanupService>();
        return services;
    }
}
