using System.Data.Common;
using Amazon.Runtime;
using Amazon.S3;
using DataHub.Application.Abstractions;
using DataHub.Application.Ingestion;
using DataHub.Application.Products;
using DataHub.Infrastructure.Messaging;
using DataHub.Infrastructure.Persistence;
using DataHub.Infrastructure.Persistence.Interceptors;
using DataHub.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace DataHub.Infrastructure;

public static class DependencyInjection
{
    public const string AppDatabase = "app";
    public const string MinioConnection = "minio";
    public const string RabbitConnection = "rabbitmq";

    /// <summary>Repositories, interceptors and object storage. Needs a registered <see cref="AppDbContext"/> (see the host overload).</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICurrentUser, SystemCurrentUser>();
        services.AddSingleton<AuditInterceptor>();
        services.AddSingleton<OutboxInterceptor>();

        services.AddScoped(typeof(IRepository<,>), typeof(Repository<,>));
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<ITemperatureStore, TemperatureStore>();

        // Aspire injects ConnectionStrings:minio as "Endpoint=...;AccessKey=...;SecretKey=...".
        services.AddOptions<ObjectStorageOptions>()
            .Configure(o =>
            {
                var cs = new DbConnectionStringBuilder { ConnectionString = configuration.GetConnectionString(MinioConnection) ?? string.Empty };
                o.Endpoint = cs.TryGetValue("Endpoint", out var endpoint) ? ((string)endpoint).TrimEnd('/') : string.Empty;
                o.AccessKey = cs.TryGetValue("AccessKey", out var access) ? (string)access : string.Empty;
                o.SecretKey = cs.TryGetValue("SecretKey", out var secret) ? (string)secret : string.Empty;
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IAmazonS3>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;
            return new AmazonS3Client(
                new BasicAWSCredentials(o.AccessKey, o.SecretKey),
                new AmazonS3Config { ServiceURL = o.Endpoint, ForcePathStyle = true, AuthenticationRegion = o.Region });
        });
        services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        return services;
    }

    /// <summary>
    /// Aspire host variant: pooled <see cref="AppDbContext"/> (DB-1) with Aspire health checks, tracing and retries,
    /// plus <see cref="AddInfrastructure(IServiceCollection, IConfiguration)"/>.
    /// </summary>
    public static IHostApplicationBuilder AddInfrastructure(this IHostApplicationBuilder builder)
    {
        // Connection string resolved lazily so tooling (schema export, EF design time) can build the host without a database.
        builder.Services.AddDbContextPool<AppDbContext>((sp, o) => o
            .UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString(AppDatabase)
                ?? throw new InvalidOperationException($"Connection string '{AppDatabase}' is missing."))
            .AddInterceptors(sp.GetRequiredService<AuditInterceptor>(), sp.GetRequiredService<OutboxInterceptor>()));
        builder.EnrichNpgsqlDbContext<AppDbContext>();
        builder.Services.AddInfrastructure(builder.Configuration);
        return builder;
    }

    /// <summary>RabbitMQ connection (Aspire), publisher, queue admin and the outbox publisher.</summary>
    public static IHostApplicationBuilder AddMessaging(this IHostApplicationBuilder builder)
    {
        builder.AddRabbitMQClient(RabbitConnection);
        builder.Services.AddOptions<MessagingOptions>()
            .Bind(builder.Configuration.GetSection(MessagingOptions.Section))
            .PostConfigure(o =>
            {
                // Username/password default to the ones in the AMQP connection string.
                if (Uri.TryCreate(builder.Configuration.GetConnectionString(RabbitConnection), UriKind.Absolute, out var amqp)
                    && amqp.UserInfo.Split(':', 2) is [var user, var password])
                {
                    o.Username = o.Username is { Length: > 0 } ? o.Username : Uri.UnescapeDataString(user);
                    o.Password = o.Password is { Length: > 0 } ? o.Password : Uri.UnescapeDataString(password);
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<RabbitMessagePublisher>();
        builder.Services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<RabbitMessagePublisher>());
        builder.Services.AddHttpClient<IQueueAdmin, RabbitQueueAdmin>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<MessagingOptions>>().Value;
            http.BaseAddress = new Uri(o.ManagementUrl.TrimEnd('/') + "/");
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{o.Username}:{o.Password}")));
        }).AddTypedClient<IQueueAdmin>((http, sp) => new RabbitQueueAdmin(sp.GetRequiredService<IConnection>(), http));
        builder.Services.AddHostedService<OutboxPublisher>();
        return builder;
    }
}
