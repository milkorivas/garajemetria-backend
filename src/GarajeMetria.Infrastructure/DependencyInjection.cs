using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Diagnostics;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Stays;
using GarajeMetria.Application.Users;
using GarajeMetria.Application.Workshops;
using GarajeMetria.Infrastructure.Authentication;
using GarajeMetria.Infrastructure.Diagnostics;
using GarajeMetria.Infrastructure.Lpr;
using GarajeMetria.Infrastructure.Persistence;
using GarajeMetria.Infrastructure.Stays;
using GarajeMetria.Infrastructure.Workshops;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

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
        services.AddHistory(configuration);
        services.AddStayProcessing(configuration, runWorker: true);
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
        services.AddOpenIddict().AddCore(options =>
        {
            options.UseEntityFrameworkCore().UseDbContext<AuthenticationDbContext>();
            options.DisableEntityCaching();
        });
        var lprPersistence = configuration.GetSection("Lpr").Get<LprPersistenceOptions>() ?? new();
        lprPersistence.Validate();
        services.AddSingleton(lprPersistence);
        var (lpr, deviceOAuth) = LprConfiguration.Read(configuration);
        services.AddSingleton(lpr);
        services.AddSingleton(deviceOAuth);
        services.AddSingleton(new DeviceTokenLifetime(TimeSpan.FromMinutes(deviceOAuth.AccessTokenMinutes)));
        services.AddSingleton<DeviceOAuthCertificates>();
        services.AddKeyedSingleton<SigningCredentials>("lpr", (sp, _) => new X509SigningCredentials(
            sp.GetRequiredService<DeviceOAuthCertificates>().Signing, SecurityAlgorithms.RsaSha256));
        services.AddKeyedSingleton<EncryptingCredentials>("lpr", (sp, _) => new X509EncryptingCredentials(
            sp.GetRequiredService<DeviceOAuthCertificates>().Encryption, SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
        services.AddHostedService<DeviceOAuthStartup>();
        services.AddScoped<IDeviceCredentialService, DeviceCredentialService>();
        services.AddScoped<ILprRateLimitStore, LprRateLimitStore>();
        services.AddScoped<LprRepository>();
        services.AddScoped<ILprDeviceRepository>(sp => sp.GetRequiredService<LprRepository>());
        services.AddScoped<ILprEventRepository, PolicyCheckedLprEventRepository>();
        services.AddScoped<ILprDeviceAdministrationQuery>(sp => sp.GetRequiredService<LprRepository>());
        services.AddScoped<IDeviceAuthorizationQuery>(sp => sp.GetRequiredService<LprRepository>());
        services.AddScoped<IDeviceAdministrationAuditRepository>(sp => sp.GetRequiredService<LprRepository>());
        services.AddScoped<ILprClock, LprClock>();
        services.AddScoped<ILprUnitOfWork, LprUnitOfWork>();
        services.AddScoped<RateLimitStore>();
        services.AddScoped<IAuthenticationRateLimitStore>(sp => sp.GetRequiredService<RateLimitStore>());
        services.AddScoped<IAuthenticationRetentionStore, RetentionStore>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<ICorporateDirectory, LdapCorporateDirectory>();
        services.AddHostedService<SharedPolicyStartup>();
        services.AddHostedService<AuthenticationCleanupService>();
        return services;
    }

    public static IServiceCollection AddHistory(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("History").Get<GarajeMetria.Application.History.HistoryOptions>() ?? new();
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<HistoryInstanceGate>();
        services.AddSingleton<GarajeMetria.Application.History.IHistoryCalendar, History.HistoryCalendar>();
        services.AddScoped<HistoryStore>();
        services.AddScoped<GarajeMetria.Application.History.IHistoryCatalogQuery>(sp => sp.GetRequiredService<HistoryStore>());
        services.AddScoped<GarajeMetria.Application.History.IHistoryReadSession>(sp => sp.GetRequiredService<HistoryStore>());
        services.AddScoped<GarajeMetria.Application.History.IHistoryClosureDiscovery>(sp => sp.GetRequiredService<HistoryStore>());
        services.AddScoped<GarajeMetria.Application.History.IHistoryAccessAuditStore>(sp => sp.GetRequiredService<HistoryStore>());
        services.AddScoped<GarajeMetria.Application.History.IHistoryQuery, HistoryQuery>();
        services.AddScoped<GarajeMetria.Application.History.IHistoryAdmission, HistoryAdmission>();
        services.AddScoped<GarajeMetria.Application.History.IHistoryWorkbookWriter, History.HistoryWorkbookWriter>();
        return services;
    }

    public static IServiceCollection AddStayMaintenance(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddLogging();
        var persistence = configuration.GetSection("Lpr").Get<LprPersistenceOptions>() ?? new();
        persistence.Validate();
        services.AddSingleton(persistence);
        var connection = configuration.GetConnectionString("Authentication");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Maintenance requires a persistence connection.");
        services.AddDbContext<AuthenticationDbContext>(o => o.UseSqlServer(connection, sql => sql.CommandTimeout(persistence.RequestTimeoutSeconds))
            .AddInterceptors(new PersistenceErrorInterceptor(), new PersistenceConnectionInterceptor()));
        services.AddScoped<ILprClock, LprClock>();
        return services.AddStayProcessing(configuration, runWorker: false);
    }

    private static IServiceCollection AddStayProcessing(this IServiceCollection services, IConfiguration configuration, bool runWorker)
    {
        var stays = configuration.GetSection("Stays").Get<StayOptions>() ?? new();
        stays.Validate();
        services.AddSingleton(stays);
        services.AddSingleton<IWorkshopCalendar>(new WorkshopCalendar());
        services.AddHostedService<StayConfigurationStartup>();
        GarajeMetria.Application.DependencyInjection.AddStayClosureValidation(services);
        services.AddScoped<IStayPolicyRepository, StayPolicyRepository>();
        services.AddSingleton(new StayProcessingLimits(stays.ObservationPageSize, stays.ContextObservationLimit, stays.ContextStayLimit));
        services.AddSingleton<IStayProcessingObserver, StayTelemetry>();
        services.AddScoped<StayRepository>();
        services.AddScoped<IStayRepository>(sp => sp.GetRequiredService<StayRepository>());
        services.AddScoped<IStayObservationQuery>(sp => sp.GetRequiredService<StayRepository>());
        services.AddScoped<IStayClosureRepository, StayClosureRepository>();
        services.AddScoped<IStayClosureUnitOfWork, StayClosureUnitOfWork>();
        services.AddScoped<IStayMaintenanceStore, StayMaintenanceStore>();
        services.AddScoped<IStayDiagnostics, StayDiagnostics>();
        services.AddScoped<IObservationAuditQuery, ObservationAuditQuery>();
        services.AddScoped<IStayRecalculationService, StayRecalculationService>();
        services.AddScoped<IObservationLocationWriter, ObservationLocationWriter>();
        if (runWorker) services.AddHostedService<StayDailyClosureWorker>();
        return services;
    }
}
