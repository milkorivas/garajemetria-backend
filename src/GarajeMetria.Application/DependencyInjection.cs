using FluentValidation;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Diagnostics;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace GarajeMetria.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(c => c.RegisterServicesFromAssemblyContaining<LoginCommand>());
        // Closure is enabled with its calendar adapter in T004-09, before registering the worker/handler.
        services.AddValidatorsFromAssemblyContaining<LoginValidator>(filter: result =>
            result.ValidatorType != typeof(GarajeMetria.Application.Stays.CloseStayDayValidator));
        services.AddScoped<GarajeMetria.Application.Users.UserAdministrationAccess>();
        services.AddScoped<GarajeMetria.Application.Lpr.DeviceAdministration>();
        services.AddScoped<GarajeMetria.Application.Lpr.DeviceAccess>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(GarajeMetria.Application.Lpr.LprAdministrationAuthorizationBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(GarajeMetria.Application.Lpr.LprDeviceAuthorizationBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(GarajeMetria.Application.Lpr.LprEventQuotaBehavior<,>));
        return services;
    }

    public static IServiceCollection AddStayClosureValidation(this IServiceCollection services)
    {
        // The composition root must also register IWorkshopCalendar and ILprClock.
        services.AddScoped<IValidator<GarajeMetria.Application.Stays.CloseStayDayCommand>,
            GarajeMetria.Application.Stays.CloseStayDayValidator>();
        return services;
    }
}
