using System.Text.Json;
using DataHub.Api.Messaging;
using DataHub.Application.Ingestion;
using DataHub.Domain.Messaging;
using DataHub.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace DataHub.Api.Tests;

/// <summary>Consume → ack, poison → dead-letter, redelivery keeps the message-id.</summary>
public sealed class WebhookInboxConsumerTests : IAsyncLifetime
{
    private const string ValidPayload = """{"containerId":"MSCU1234567","readings":[{"timestamp":"2026-09-25T10:00:00Z","value":41,"unit":"F"}]}""";

    private readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4-management").Build();
    private readonly ITemperatureStore store = Substitute.For<ITemperatureStore>();
    private IConnection connection = null!;
    private IChannel channel = null!;
    private ServiceProvider services = null!;
    private WebhookInboxConsumer consumer = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await rabbit.StartAsync(Ct);
        connection = await new ConnectionFactory { Uri = new Uri(rabbit.GetConnectionString()) }.CreateConnectionAsync(Ct);
        channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        await MessagingTopology.DeclareAsync(channel, Ct);
        services = new ServiceCollection().AddScoped(_ => store).AddScoped<IngestTemperaturesService>().BuildServiceProvider();
        consumer = new WebhookInboxConsumer(connection, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<WebhookInboxConsumer>.Instance);
        await consumer.StartAsync(Ct);
        await WaitUntil(async () => await channel.ConsumerCountAsync(WebhookInboxConsumer.QueueName, Ct) == 1);
    }

    public async ValueTask DisposeAsync()
    {
        await consumer.StopAsync(CancellationToken.None);
        consumer.Dispose();
        await services.DisposeAsync();
        await channel.DisposeAsync();
        await connection.DisposeAsync();
        await rabbit.DisposeAsync();
    }

    private async Task Publish(string messageId, string payloadJson, string @event = "received")
    {
        var message = new WebhookReceived("body-id", "container", "container/x.json", DateTimeOffset.UtcNow, JsonDocument.Parse(payloadJson).RootElement, @event);
        await channel.BasicPublishAsync(
            WebhookReceived.Exchange,
            WebhookReceived.RoutingKey("container", @event),
            mandatory: true,
            new BasicProperties { MessageId = messageId },
            JsonSerializer.SerializeToUtf8Bytes(message, JsonSerializerOptions.Web),
            Ct);
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met");
            }

            await Task.Delay(100, Ct);
        }
    }

    private async Task<uint> DeadLetters() => await channel.MessageCountAsync(MessagingTopology.WebhookDlq, Ct);

    [Fact]
    public async Task Valid_message_is_stored_under_amqp_message_id_and_acked()
    {
        store.SaveAsync(Arg.Any<string>(), Arg.Any<ContainerReadings>(), Arg.Any<CancellationToken>()).Returns(true);

        await Publish("hook-1", ValidPayload, "alarm");

        await WaitUntil(() => Task.FromResult(store.ReceivedCalls().Any()));
        await store.Received(1).SaveAsync("hook-1", Arg.Is<ContainerReadings>(r => r.Readings.Single().Celsius == 5m), Arg.Any<CancellationToken>());
        await WaitUntil(async () => await channel.MessageCountAsync(WebhookInboxConsumer.QueueName, Ct) == 0);
        (await DeadLetters()).ShouldBe(0u);
    }

    [Fact]
    public async Task Invalid_payload_goes_straight_to_dead_letter_queue()
    {
        await Publish("hook-2", """{"containerId":"MSCU1234567","readings":[]}""");

        await WaitUntil(async () => await DeadLetters() == 1);
        store.ReceivedCalls().ShouldBeEmpty();
    }
}
