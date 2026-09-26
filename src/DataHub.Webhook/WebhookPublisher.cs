using System.Diagnostics;
using System.Text.Json;
using DataHub.Domain.Messaging;
using RabbitMQ.Client;

namespace DataHub.Webhook;

public interface IWebhookPublisher
{
    Task PublishAsync(WebhookReceived message, CancellationToken ct);
}

public sealed class RabbitWebhookPublisher(IConnection connection) : IWebhookPublisher
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(WebhookReceived message, CancellationToken ct)
    {
        // ponytail: one confirmed channel per request; pool channels if request rate grows.
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await channel.ExchangeDeclareAsync(WebhookReceived.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);

        var properties = new BasicProperties
        {
            MessageId = message.Id,
            CorrelationId = Activity.Current?.TraceId.ToString(),
            Type = nameof(WebhookReceived),
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(message.ReceivedAtUtc.ToUnixTimeSeconds()),
        };

        // mandatory: an unroutable message (no bound queue) fails the request instead of vanishing.
        await channel.BasicPublishAsync(
            WebhookReceived.Exchange,
            WebhookReceived.RoutingKey(message.Sender, message.Event),
            mandatory: true,
            properties,
            JsonSerializer.SerializeToUtf8Bytes(message, Json),
            ct);
    }
}
