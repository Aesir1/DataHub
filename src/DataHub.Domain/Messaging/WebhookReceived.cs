using System.Text.Json;

namespace DataHub.Domain.Messaging;

/// <summary>
/// Message published by the webhook for every accepted request. The raw payload is carried inline;
/// <see cref="ObjectKey"/> points at the copy stored in object storage.
/// </summary>
public sealed record WebhookReceived(
    string Id,
    string Sender,
    string ObjectKey,
    DateTimeOffset ReceivedAtUtc,
    JsonElement Payload,
    string Event = WebhookReceived.DefaultEvent)
{
    public const string Exchange = "webhooks";
    public const string DefaultEvent = "received";

    /// <summary>WH-6: <c>webhook.&lt;sender&gt;.&lt;event&gt;</c>.</summary>
    public static string RoutingKey(string sender, string @event = DefaultEvent) => $"webhook.{sender}.{@event}";
}
