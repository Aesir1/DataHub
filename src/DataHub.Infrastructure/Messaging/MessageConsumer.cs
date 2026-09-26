using System.Diagnostics;
using System.Text.Json;
using DataHub.Application.Abstractions;
using DataHub.Domain.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DataHub.Infrastructure.Messaging;

/// <summary>
/// MQ-3: acks only after <see cref="HandleAsync"/> succeeds; other failures retry 3 times with backoff, then go
/// to the dead-letter queue. <see cref="DomainException"/> and undecodable bodies are dead-lettered immediately.
/// Handlers must be idempotent on <see cref="MessageContext.MessageId"/>.
/// </summary>
public abstract class MessageConsumer<T>(IConnection connection, IServiceScopeFactory scopes, ILogger logger) : BackgroundService
    where T : class
{
    protected abstract string Queue { get; }

    /// <summary>Retry delays; the count is the number of retries.</summary>
    protected virtual TimeSpan[] Backoff { get; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    protected IServiceScopeFactory Scopes { get; } = scopes;

    /// <summary>Runs inside a fresh DI scope per delivery attempt.</summary>
    protected abstract Task HandleAsync(T message, MessageContext context, IServiceProvider services, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await MessagingTopology.DeclareAsync(channel, stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 10, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => DeliverAsync(channel, ea, stoppingToken);
        await channel.BasicConsumeAsync(Queue, autoAck: false, consumer, stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    protected virtual string MessageIdOf(T message, BasicDeliverEventArgs ea) =>
        ea.BasicProperties.MessageId ?? $"{ea.Exchange}:{ea.RoutingKey}:{ea.DeliveryTag}";

    private async Task DeliverAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var parent = MessagingTelemetry.Header(ea.BasicProperties, "traceparent");
        using var activity = MessagingTelemetry.Source.StartActivity(
            $"{Queue} process",
            ActivityKind.Consumer,
            parent is not null && ActivityContext.TryParse(parent, MessagingTelemetry.Header(ea.BasicProperties, "tracestate"), out var ctx) ? ctx : default);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", Queue);
        activity?.SetTag("messaging.message.id", ea.BasicProperties.MessageId);

        T? message;
        try
        {
            message = JsonSerializer.Deserialize<T>(ea.Body.Span, JsonSerializerOptions.Web);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Undecodable message {DeliveryTag} on {Queue}, dead-lettering", ea.DeliveryTag, Queue);
            message = null;
        }

        if (message is null)
        {
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        var context = new MessageContext(MessageIdOf(message, ea), ea.BasicProperties.CorrelationId, ea.RoutingKey, ea.Redelivered);

        // ponytail: in-process backoff blocks this channel while waiting; switch to delayed retry queues if throughput matters.
        for (var attempt = 0; attempt <= Backoff.Length; attempt++)
        {
            try
            {
                await using (var scope = Scopes.CreateAsyncScope())
                {
                    await HandleAsync(message, context, scope.ServiceProvider, ct);
                }

                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
                return;
            }
            catch (DomainException ex)
            {
                logger.LogWarning(ex, "Rejected {MessageId} on {Queue}", context.MessageId, Queue);
                break;
            }
            catch (Exception ex) when (attempt < Backoff.Length && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Attempt {Attempt} for {MessageId} on {Queue} failed, retrying", attempt + 1, context.MessageId, Queue);
                await Task.Delay(Backoff[attempt], ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Giving up on {MessageId} on {Queue} after {Attempts} attempts", context.MessageId, Queue, attempt + 1);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            }
        }

        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
    }
}

/// <summary>Consumer that resolves <see cref="IMessageHandler{T}"/> from the per-message scope.</summary>
public abstract class HandlerConsumer<T>(IConnection connection, IServiceScopeFactory scopes, ILogger logger)
    : MessageConsumer<T>(connection, scopes, logger)
    where T : class
{
    protected override Task HandleAsync(T message, MessageContext context, IServiceProvider services, CancellationToken ct) =>
        services.GetRequiredService<IMessageHandler<T>>().HandleAsync(message, context, ct);
}
