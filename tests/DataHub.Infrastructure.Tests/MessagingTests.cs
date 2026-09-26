using System.Text;
using System.Text.Json;
using DataHub.Application.Abstractions;
using DataHub.Domain.Products;
using DataHub.Infrastructure.Messaging;
using DataHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace DataHub.Infrastructure.Tests;

/// <summary>MQ-6: publish → consume → ack, retry → dead-letter, the four admin operations, and the outbox.</summary>
public sealed class MessagingTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4-management").WithPortBinding(15672, true).Build();
    private readonly IMessageHandler<ProductChanged> handler = Substitute.For<IMessageHandler<ProductChanged>>();
    private IConnection connection = null!;
    private IChannel channel = null!;
    private ServiceProvider services = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await rabbit.StartAsync(Ct);
        connection = await new ConnectionFactory { Uri = new Uri(rabbit.GetConnectionString()) }.CreateConnectionAsync(Ct);
        channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        await MessagingTopology.DeclareAsync(channel, Ct);
        services = new ServiceCollection()
            .AddScoped(_ => handler)
            .AddScoped(_ => db.Create())
            .BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        await channel.DisposeAsync();
        await connection.DisposeAsync();
        await rabbit.DisposeAsync();
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

    private Task<uint> Depth(string queue) => channel.MessageCountAsync(queue, Ct);

    private async Task<TestConsumer> StartConsumer()
    {
        var consumer = new TestConsumer(connection, services.GetRequiredService<IServiceScopeFactory>());
        await consumer.StartAsync(Ct);
        await WaitUntil(async () => await channel.ConsumerCountAsync(MessagingTopology.ProductAuditQueue, Ct) == 1);
        return consumer;
    }

    private static ProductChanged Changed() => new(Guid.CreateVersion7(), Domain.Abstractions.ChangeType.Created, "Reefer", "RF-1", 10m, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Published_message_is_consumed_and_acked_with_headers()
    {
        using var consumer = await StartConsumer();
        MessageContext? seen = null;
        handler.HandleAsync(Arg.Any<ProductChanged>(), Arg.Do<MessageContext>(c => seen = c), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        using (var activity = new System.Diagnostics.Activity("test").Start())
        {
            await new RabbitMessagePublisher(connection).PublishAsync(Changed(), "product.created", "msg-1", Ct);
        }

        await WaitUntil(() => Task.FromResult(seen is not null));
        seen!.MessageId.ShouldBe("msg-1");
        seen.RoutingKey.ShouldBe("product.created");
        seen.CorrelationId.ShouldNotBeNullOrEmpty();
        await WaitUntil(async () => await Depth(MessagingTopology.ProductAuditQueue) == 0);
        (await Depth(MessagingTopology.DeadLetterQueue(MessagingTopology.ProductAuditQueue))).ShouldBe(0u);
        await consumer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Failing_handler_retries_then_dead_letters()
    {
        using var consumer = await StartConsumer();
        handler.HandleAsync(Arg.Any<ProductChanged>(), Arg.Any<MessageContext>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("db down"));

        await new RabbitMessagePublisher(connection).PublishAsync(Changed(), "product.updated", ct: Ct);

        await WaitUntil(async () => await Depth(MessagingTopology.DeadLetterQueue(MessagingTopology.ProductAuditQueue)) == 1);
        handler.ReceivedCalls().Count().ShouldBe(4); // first try + 3 retries
        await consumer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Domain_exception_dead_letters_without_retry()
    {
        using var consumer = await StartConsumer();
        handler.HandleAsync(Arg.Any<ProductChanged>(), Arg.Any<MessageContext>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new Domain.Exceptions.ConflictException("no"));

        await new RabbitMessagePublisher(connection).PublishAsync(Changed(), "product.deleted", ct: Ct);

        await WaitUntil(async () => await Depth(MessagingTopology.DeadLetterQueue(MessagingTopology.ProductAuditQueue)) == 1);
        handler.ReceivedCalls().Count().ShouldBe(1);
        await consumer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Queue_admin_declare_list_purge_delete()
    {
        using var management = new HttpClient { BaseAddress = new Uri($"http://{rabbit.Hostname}:{rabbit.GetMappedPublicPort(15672)}/") };
        management.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("rabbitmq:rabbitmq")));
        var admin = new RabbitQueueAdmin(connection, management);

        await admin.DeclareQueueAsync(new QueueDefinition("test-queue", MessagingTopology.DomainEvents, "test.*"), Ct);
        await new RabbitMessagePublisher(connection).PublishAsync(new { hello = 1 }, "test.one", ct: Ct);
        await WaitUntil(async () => (await admin.ListQueuesAsync(Ct)).Any(q => q.Name == "test-queue" && q.Messages == 1), seconds: 30);

        (await admin.PurgeQueueAsync("test-queue", Ct)).ShouldBe(1u);
        (await Depth("test-queue")).ShouldBe(0u);

        await admin.DeleteQueueAsync("test-queue", ct: Ct);
        await WaitUntil(async () => (await admin.ListQueuesAsync(Ct)).All(q => q.Name != "test-queue"), seconds: 30);
    }

    [Fact]
    public async Task Outbox_publisher_publishes_committed_rows_once()
    {
        var queue = (await channel.QueueDeclareAsync(cancellationToken: Ct)).QueueName;
        await channel.QueueBindAsync(queue, MessagingTopology.DomainEvents, "product.*", cancellationToken: Ct);
        await using (var ctx = db.Create())
        {
            await new Repository<Product, Guid>(ctx).AddAsync(new Product { Name = "Reefer", Sku = "OUT-1", Price = 1m }, Ct);
        }

        var outbox = new OutboxPublisher(services.GetRequiredService<IServiceScopeFactory>(), new RabbitMessagePublisher(connection), db.Time, NullLogger<OutboxPublisher>.Instance);
        (await outbox.PublishBatchAsync(Ct)).ShouldBe(1);
        (await outbox.PublishBatchAsync(Ct)).ShouldBe(0);

        var message = await channel.BasicGetAsync(queue, autoAck: true, Ct);
        message!.RoutingKey.ShouldBe("product.created");
        message.BasicProperties.Type.ShouldBe(nameof(ProductChanged));
        JsonSerializer.Deserialize<ProductChanged>(message.Body.Span, JsonSerializerOptions.Web)!.Sku.ShouldBe("OUT-1");
        await using var check = db.Create();
        (await check.OutboxMessages.SingleAsync(Ct)).Id.ToString().ShouldBe(message.BasicProperties.MessageId);
    }

    [Fact]
    public async Task Outbox_keeps_rows_when_broker_is_down()
    {
        await using (var ctx = db.Create())
        {
            await new Repository<Product, Guid>(ctx).AddAsync(new Product { Name = "Reefer", Sku = "OUT-2", Price = 1m }, Ct);
        }

        await connection.CloseAsync(Ct);
        var outbox = new OutboxPublisher(services.GetRequiredService<IServiceScopeFactory>(), new RabbitMessagePublisher(connection), db.Time, NullLogger<OutboxPublisher>.Instance);

        await Should.ThrowAsync<Exception>(() => outbox.PublishBatchAsync(Ct));
        await using var check = db.Create();
        var row = await check.OutboxMessages.SingleAsync(Ct);
        row.PublishedAtUtc.ShouldBeNull();
        row.Attempts.ShouldBe(1);
    }

    private sealed class TestConsumer(IConnection connection, IServiceScopeFactory scopes)
        : HandlerConsumer<ProductChanged>(connection, scopes, NullLogger.Instance)
    {
        protected override string Queue => MessagingTopology.ProductAuditQueue;

        protected override TimeSpan[] Backoff { get; } = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)];
    }
}
