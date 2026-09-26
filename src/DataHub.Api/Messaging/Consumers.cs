using DataHub.Application.Abstractions;
using DataHub.Application.Ingestion;
using DataHub.Domain.Messaging;
using DataHub.Domain.Products;
using DataHub.Infrastructure.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DataHub.Api.Messaging;

/// <summary>WH-6: persists webhook payloads from <c>webhooks.container</c> as containers and Celsius readings.</summary>
public sealed class WebhookInboxConsumer(IConnection connection, IServiceScopeFactory scopes, ILogger<WebhookInboxConsumer> logger)
    : MessageConsumer<WebhookReceived>(connection, scopes, logger)
{
    public const string QueueName = MessagingTopology.WebhookQueue;

    protected override string Queue => QueueName;

    // WH-4: the webhook sets message-id from X-Webhook-Id; fall back to the id in the body.
    protected override string MessageIdOf(WebhookReceived message, BasicDeliverEventArgs ea) => ea.BasicProperties.MessageId ?? message.Id;

    protected override async Task HandleAsync(WebhookReceived message, MessageContext context, IServiceProvider services, CancellationToken ct)
    {
        var stored = await services.GetRequiredService<IngestTemperaturesService>().IngestAsync(message with { Id = context.MessageId }, ct);
        logger.LogInformation("Webhook {MessageId} ({ObjectKey}) stored: {Stored}", context.MessageId, message.ObjectKey, stored);
    }
}

/// <summary>Stores an audit row for every product.* domain event.</summary>
public sealed class ProductAuditConsumer(IConnection connection, IServiceScopeFactory scopes, ILogger<ProductAuditConsumer> logger)
    : HandlerConsumer<ProductChanged>(connection, scopes, logger)
{
    protected override string Queue => MessagingTopology.ProductAuditQueue;
}
