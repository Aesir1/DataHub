using System.Text.Json;
using DataHub.Domain.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client.Exceptions;

namespace DataHub.Webhook;

public sealed class ReceiveWebhook(
    WebhookAuthenticator auth,
    IPayloadStore store,
    IWebhookPublisher publisher,
    TimeProvider time,
    ILogger<ReceiveWebhook> logger)
{
    public const int MaxBodyBytes = 1_048_576;
    public const string IdHeader = "X-Webhook-Id";
    public const string EventHeader = "X-Webhook-Event";

    [Function(nameof(ReceiveWebhook))]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhooks/{sender}")] HttpRequest req,
        string sender,
        CancellationToken ct)
    {
        var body = await ReadBodyAsync(req, ct);
        if (body is null)
        {
            return new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);
        }

        if (!await auth.IsAuthorizedAsync(sender, req.Headers, body, ct))
        {
            return new UnauthorizedResult();
        }

        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(body);
            payload = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult(new { error = "Body must be valid JSON." });
        }

        var id = req.Headers[IdHeader].ToString() is { Length: > 0 and <= 128 } given ? given : Guid.CreateVersion7().ToString();
        var @event = EventName(req.Headers[EventHeader].ToString());
        if (@event is null)
        {
            return new BadRequestObjectResult(new { error = $"{EventHeader} must be 1-64 characters a-z, 0-9, '-' or '_'." });
        }

        var key = await store.PutAsync(sender, body, ct);
        try
        {
            await publisher.PublishAsync(new WebhookReceived(id, sender, key, time.GetUtcNow(), payload, @event), ct);
        }
        catch (Exception ex) when (ex is PublishException or BrokerUnreachableException or AlreadyClosedException)
        {
            // Payload is safe in object storage; the sender retries.
            logger.LogError(ex, "Publishing webhook {Id} ({Key}) failed", id, key);
            return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation("Webhook {Id} from {Sender} stored as {Key}", id, sender, key);
        return new AcceptedResult(location: null, value: new { id });
    }

    /// <summary>Routing-key segment from <see cref="EventHeader"/>; <c>received</c> when absent, null when invalid.</summary>
    internal static string? EventName(string header)
    {
        if (header.Length == 0)
        {
            return WebhookReceived.DefaultEvent;
        }

        var name = header.Trim().ToLowerInvariant();
        return name.Length <= 64 && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_') ? name : null;
    }

    /// <summary>Reads the exact request bytes (needed for HMAC); null when larger than <see cref="MaxBodyBytes"/>.</summary>
    internal static async Task<byte[]?> ReadBodyAsync(HttpRequest req, CancellationToken ct)
    {
        if (req.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await req.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return buffer.ToArray();
    }
}
