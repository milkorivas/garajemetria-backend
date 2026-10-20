using FluentValidation;
using GarajeMetria.Application.Authentication;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace GarajeMetria.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(c => c.RegisterServicesFromAssemblyContaining<LoginCommand>());
        services.AddValidatorsFromAssemblyContaining<LoginValidator>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        return services;
    }
}
