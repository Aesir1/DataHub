using DataHub.Api.GraphQl;
using DataHub.Api.Messaging;
using DataHub.Infrastructure.Storage;

namespace DataHub.Api;

public static class DependencyInjection
{
    /// <summary>GraphQL schema, message consumers and startup work of the Api host.</summary>
    public static IServiceCollection AddApi(this IServiceCollection services, IConfiguration configuration, bool isDevelopment)
    {
        _ = configuration;
        services.AddHostedService<BucketInitializer>();
        services.AddHostedService<WebhookInboxConsumer>();
        services.AddHostedService<ProductAuditConsumer>();
        services.AddDataHubGraphQl(isDevelopment);
        return services;
    }
}
