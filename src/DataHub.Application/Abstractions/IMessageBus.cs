namespace DataHub.Application.Abstractions;

public interface IMessagePublisher
{
    /// <summary>Publishes JSON with message-id, correlation-id and type headers; completes after the broker confirms.</summary>
    Task PublishAsync<T>(T message, string routingKey, string? messageId = null, CancellationToken ct = default)
        where T : class;
}

public sealed record QueueDefinition(string Name, string? Exchange = null, string? RoutingKey = null, bool DeadLetter = true);

public sealed record QueueInfo(string Name, uint Messages, uint Consumers);

public interface IQueueAdmin
{
    Task DeclareQueueAsync(QueueDefinition definition, CancellationToken ct = default);

    Task<IReadOnlyList<QueueInfo>> ListQueuesAsync(CancellationToken ct = default);

    Task<uint> PurgeQueueAsync(string queue, CancellationToken ct = default);

    Task DeleteQueueAsync(string queue, bool ifUnused = true, CancellationToken ct = default);
}

public sealed record MessageContext(string MessageId, string? CorrelationId, string RoutingKey, bool Redelivered);

/// <summary>Handles one message type; the Infrastructure consumer acks only after this returns.</summary>
public interface IMessageHandler<in T>
    where T : class
{
    Task HandleAsync(T message, MessageContext context, CancellationToken ct);
}
