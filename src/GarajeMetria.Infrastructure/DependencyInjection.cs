using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Diagnostics;
using GarajeMetria.Application.Users;
using GarajeMetria.Application.Workshops;
using GarajeMetria.Infrastructure.Authentication;
using GarajeMetria.Infrastructure.Diagnostics;
using GarajeMetria.Infrastructure.Persistence;
using GarajeMetria.Infrastructure.Workshops;
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
        var catalog = configuration.GetSection("WorkshopCatalog").Get<WorkshopCatalogOptions>() ?? new();
        if (catalog.IntervalSeconds is <= 0 or > 600 || catalog.TimeoutSeconds is <= 0 or > 120 ||
            catalog.IntervalSeconds + catalog.TimeoutSeconds > 840 || catalog.DefaultPageSize <= 0 ||
            catalog.MaxPageSize is <= 0 or > 100 || catalog.DefaultPageSize > catalog.MaxPageSize ||
            catalog.Enabled && catalog.Source != "SqlServer" || !catalog.Enabled && catalog.Source != "Disabled")
            throw new InvalidOperationException("Invalid WorkshopCatalog configuration.");
        services.AddSingleton(catalog);
        var administration = configuration.GetSection("UserAdministration").Get<UserAdministrationOptions>() ?? new();
        if (administration.DefaultPageSize <= 0 || administration.MaxPageSize is <= 0 or > 100 ||
            administration.DefaultPageSize > administration.MaxPageSize || administration.MaxBodyBytes is <= 0 or > 1048576)
            throw new InvalidOperationException("Invalid UserAdministration configuration.");
        services.AddSingleton(administration);
        services.AddScoped<UserAdministrationRepository>();
        services.AddScoped<IUserAdministrationQuery>(sp => sp.GetRequiredService<UserAdministrationRepository>());
        services.AddScoped<ILocalUserRepository>(sp => sp.GetRequiredService<UserAdministrationRepository>());
        services.AddScoped<IWorkshopQuery>(sp => sp.GetRequiredService<UserAdministrationRepository>());
        services.AddScoped<IWorkshopAssignmentAuditRepository>(sp => sp.GetRequiredService<UserAdministrationRepository>());
        services.AddScoped<IWorkshopCatalogStore, SqlWorkshopCatalogStore>();
        if (catalog.Enabled)
        {
            var sourceOptions = configuration.GetSection("WorkshopSource").Get<WorkshopSourceOptions>() ?? new();
            var sourceConnection = configuration.GetConnectionString("WorkshopSource");
            if (string.IsNullOrWhiteSpace(sourceConnection) || sourceOptions.CommandTimeoutSeconds is <= 0 or > 120 ||
                sourceOptions.CommandTimeoutSeconds > catalog.TimeoutSeconds)
                throw new InvalidOperationException("Invalid or missing WorkshopSource configuration.");
            try
            {
                var sourceBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sourceConnection);
                if (sourceBuilder.ConnectTimeout is <= 0 or > 120)
                    throw new InvalidOperationException("WorkshopSource requires a bounded connection timeout.");
            }
            catch (ArgumentException) { throw new InvalidOperationException("Invalid WorkshopSource connection configuration."); }
            services.AddSingleton(sourceOptions);
            services.AddDbContext<WorkshopSourceDbContext>(o => o.UseSqlServer(sourceConnection,
                sql => sql.CommandTimeout(sourceOptions.CommandTimeoutSeconds)));
            services.AddScoped<IWorkshopSource, SqlWorkshopSource>();
            services.AddHostedService<WorkshopSynchronizationService>();
        }
        else
        {
            services.AddSingleton<IWorkshopSource, DisabledWorkshopSource>();
        }
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
        services.AddSingleton<HealthStatusTracker>();
        services.AddScoped<HealthDiagnostics>();
        services.AddScoped<IHealthDiagnostics>(sp => new HealthDiagnosticsLoggingDecorator(
            sp.GetRequiredService<HealthDiagnostics>(), sp.GetRequiredService<HealthStatusTracker>()));
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
