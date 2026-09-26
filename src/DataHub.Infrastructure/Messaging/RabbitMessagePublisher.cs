using System.Diagnostics;
using System.Text.Json;
using DataHub.Application.Abstractions;
using RabbitMQ.Client;

namespace DataHub.Infrastructure.Messaging;

/// <summary>MQ-2: JSON body, message-id, correlation-id (trace id) and type headers, publisher confirms.</summary>
public sealed class RabbitMessagePublisher(IConnection connection) : IMessagePublisher
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task PublishAsync<T>(T message, string routingKey, string? messageId = null, CancellationToken ct = default)
        where T : class =>
        PublishJsonAsync(
            MessagingTopology.DomainEvents,
            routingKey,
            typeof(T).Name,
            messageId ?? Guid.CreateVersion7().ToString(),
            JsonSerializer.SerializeToUtf8Bytes(message, Json),
            parentTraceId: null,
            ct);

    /// <summary>Raw publish used by the outbox; <paramref name="parentTraceId"/> links the publish to the originating request.</summary>
    public async Task PublishJsonAsync(string exchange, string routingKey, string type, string messageId, byte[] body, string? parentTraceId, CancellationToken ct)
    {
        using var activity = MessagingTelemetry.Source.StartActivity(
            $"{routingKey} publish",
            ActivityKind.Producer,
            parentTraceId is not null && ActivityContext.TryParse(parentTraceId, null, out var parent) ? parent : default);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", exchange);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", routingKey);
        activity?.SetTag("messaging.message.id", messageId);

        // ponytail: one confirmed channel per publish; pool channels if publish rate grows.
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);

        var headers = new Dictionary<string, object?>();
        MessagingTelemetry.Inject(activity ?? Activity.Current, headers);
        var properties = new BasicProperties
        {
            MessageId = messageId,
            CorrelationId = (activity ?? Activity.Current)?.TraceId.ToString(),
            Type = type,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Headers = headers,
        };

        await channel.BasicPublishAsync(exchange, routingKey, mandatory: true, properties, body, ct);
    }
}
