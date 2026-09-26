using DataHub.Auth;
using DataHub.DbUtils.Contexts;
using DataHub.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataHub.DbUtils;

public static class DependencyInjection
{
    public static IServiceCollection AddDbUtils(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<AppContextCommands>();
        services.AddScoped<AuthContextCommands>();
        services.AddOptions<SeedOptions>().Bind(configuration.GetSection(SeedOptions.Section));
        services.AddHttpClient<KeycloakUsers>(c => c.BaseAddress = new Uri("https+http://keycloak/"))
            .AddTypedClient((http, sp) => new KeycloakUsers(
                http,
                configuration.GetSection(SeedOptions.Section).Get<SeedOptions>() ?? new SeedOptions(),
                sp.GetRequiredService<ILogger<KeycloakUsers>>()));
        return services;
    }

    /// <summary>Aspire host variant: both pooled DbContexts, object storage, and the commands.</summary>
    public static IHostApplicationBuilder AddDbUtils(this IHostApplicationBuilder builder)
    {
        builder.AddInfrastructure();
        builder.AddAuthDbContext();
        builder.Services.AddDbUtils(builder.Configuration);
        return builder;
    }
}
