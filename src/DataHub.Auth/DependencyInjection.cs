using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using DataHub.Application.Abstractions;
using DataHub.Auth.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DataHub.Auth;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    [Required]
    public string Realm { get; set; } = "datahub";

    [Required]
    public string Audience { get; set; } = "api";

    /// <summary>Keycloak runs on plain HTTP locally; set false in appsettings.Development.json only.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}

public static class DependencyInjection
{
    public const string AuthDatabase = "auth";
    public const string KeycloakService = "keycloak";

    /// <summary>JWT bearer against Keycloak (AU-10), claims transformation, one policy per permission and <see cref="ICurrentUser"/>.</summary>
    public static IServiceCollection AddDataHubAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.Section)).ValidateDataAnnotations().ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddKeycloakJwtBearer(KeycloakService, options.Realm, o =>
            {
                o.Audience = options.Audience;
                o.RequireHttpsMetadata = options.RequireHttpsMetadata;
                o.MapInboundClaims = false;
                o.TokenValidationParameters.NameClaimType = "preferred_username";
                o.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
                o.TokenValidationParameters.ValidAlgorithms = ["RS256"];
            });

        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.AddScoped<IClaimsTransformation, KeycloakClaimsTransformation>();
        services.Replace(ServiceDescriptor.Singleton<ICurrentUser, CurrentUser>());

        var authorization = services.AddAuthorizationBuilder();
        foreach (var permission in Permissions.All)
        {
            authorization.AddPolicy(permission, p => p.RequireAuthenticatedUser().RequireClaim(Permissions.ClaimType, permission));
        }

        return services;
    }

    /// <summary>Aspire host variant: pooled <see cref="AuthDbContext"/> on database <c>auth</c> with Aspire telemetry.</summary>
    public static IHostApplicationBuilder AddDataHubAuth(this IHostApplicationBuilder builder)
    {
        builder.AddAuthDbContext();
        builder.Services.AddDataHubAuth(builder.Configuration);
        return builder;
    }

    /// <summary>Only the pooled <see cref="AuthDbContext"/> (used by DbUtils, which needs no authentication).</summary>
    public static IHostApplicationBuilder AddAuthDbContext(this IHostApplicationBuilder builder)
    {
        builder.Services.AddDbContextPool<AuthDbContext>((sp, o) => o.UseNpgsql(
            sp.GetRequiredService<IConfiguration>().GetConnectionString(AuthDatabase)
                ?? throw new InvalidOperationException($"Connection string '{AuthDatabase}' is missing."),
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", AuthDbContext.Schema)));
        builder.EnrichNpgsqlDbContext<AuthDbContext>();
        return builder;
    }
}
