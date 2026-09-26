using System.Data.Common;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace DataHub.Webhook;

public static class DependencyInjection
{
    public static IHostApplicationBuilder AddWebhook(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddSingleton(TimeProvider.System);

        services.AddOptions<WebhookOptions>().Bind(builder.Configuration.GetSection(WebhookOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();

        // Aspire injects ConnectionStrings:minio as "Endpoint=...;AccessKey=...;SecretKey=...".
        services.AddOptions<ObjectStorageOptions>().Configure(o =>
        {
            var cs = new DbConnectionStringBuilder { ConnectionString = builder.Configuration.GetConnectionString("minio") ?? string.Empty };
            o.Endpoint = cs.TryGetValue("Endpoint", out var endpoint) ? (string)endpoint : string.Empty;
            o.AccessKey = cs.TryGetValue("AccessKey", out var access) ? (string)access : string.Empty;
            o.SecretKey = cs.TryGetValue("SecretKey", out var secret) ? (string)secret : string.Empty;
        }).ValidateDataAnnotations().ValidateOnStart();

        services.AddSingleton<IAmazonS3>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;
            return new AmazonS3Client(
                new BasicAWSCredentials(o.AccessKey, o.SecretKey),
                new AmazonS3Config { ServiceURL = o.Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        });

        services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<WebhookOptions>>().Value;
            return new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{o.Authority.TrimEnd('/')}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = !builder.Environment.IsDevelopment() });
        });

        services.AddSingleton<BearerVerifier>();
        services.AddSingleton<WebhookAuthenticator>();
        services.AddSingleton<IPayloadStore, S3PayloadStore>();
        services.AddSingleton<IWebhookPublisher, RabbitWebhookPublisher>();
        return builder;
    }
}
