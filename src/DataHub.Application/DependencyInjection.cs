using DataHub.Application.Abstractions;
using DataHub.Application.Documents;
using DataHub.Application.Ingestion;
using DataHub.Application.Products;
using DataHub.Domain.Products;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DataHub.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        _ = configuration;
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IngestTemperaturesService>();
        services.AddScoped<ProductService>();
        services.AddScoped<DocumentService>();
        services.AddScoped<IMessageHandler<ProductChanged>, ProductAuditHandler>();
        return services;
    }
}
