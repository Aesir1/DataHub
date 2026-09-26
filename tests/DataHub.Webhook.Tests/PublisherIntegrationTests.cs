using System.Text.Json;
using DataHub.Domain.Messaging;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace DataHub.Webhook.Tests;

/// <summary>WH-23: a verified request publishes exactly one message with the expected routing key and message-id.</summary>
public sealed class PublisherIntegrationTests : IAsyncLifetime
{
    private readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4-management").Build();
    private IConnection connection = null!;

    public async ValueTask InitializeAsync()
    {
        await rabbit.StartAsync(TestContext.Current.CancellationToken);
        connection = await new ConnectionFactory { Uri = new Uri(rabbit.GetConnectionString()) }.CreateConnectionAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        await rabbit.DisposeAsync();
    }

    [Fact]
    public async Task Publishes_one_message_with_routing_key_and_message_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.ExchangeDeclareAsync(WebhookReceived.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        var queue = (await channel.QueueDeclareAsync(cancellationToken: ct)).QueueName;
        await channel.QueueBindAsync(queue, WebhookReceived.Exchange, "webhook.container.received", cancellationToken: ct);

        var payload = JsonDocument.Parse("""{"containerId":"MSCU1234567"}""").RootElement;
        await new RabbitWebhookPublisher(connection).PublishAsync(
            new WebhookReceived("hook-42", "container", "container/x.json", DateTimeOffset.UtcNow, payload), ct);

        var message = await channel.BasicGetAsync(queue, autoAck: true, ct);
        message.ShouldNotBeNull();
        message.RoutingKey.ShouldBe("webhook.container.received");
        message.BasicProperties.CorrelationId.ShouldBeNull(); // no ambient trace in this test
        message.BasicProperties.MessageId.ShouldBe("hook-42");
        message.BasicProperties.Type.ShouldBe(nameof(WebhookReceived));
        JsonSerializer.Deserialize<WebhookReceived>(message.Body.Span, JsonSerializerOptions.Web)!.ObjectKey.ShouldBe("container/x.json");
        (await channel.MessageCountAsync(queue, ct)).ShouldBe(0u);
    }

    [Fact]
    public async Task Unroutable_message_fails_instead_of_vanishing()
    {
        var payload = JsonDocument.Parse("{}").RootElement;

        await Should.ThrowAsync<RabbitMQ.Client.Exceptions.PublishException>(() => new RabbitWebhookPublisher(connection).PublishAsync(
            new WebhookReceived("hook-43", "nobody-listens", "nobody-listens/x.json", DateTimeOffset.UtcNow, payload),
            TestContext.Current.CancellationToken));
    }
}
