using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DataHub.Application.Abstractions;
using RabbitMQ.Client;

namespace DataHub.Infrastructure.Messaging;

public sealed class MessagingOptions
{
    public const string Section = "Messaging";

    /// <summary>RabbitMQ management API base URL, for example http://localhost:15672.</summary>
    [Required]
    [Url]
    public string ManagementUrl { get; set; } = string.Empty;

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

/// <summary>Create/purge/delete over AMQP; list over the management API (AMQP cannot enumerate queues).</summary>
public sealed class RabbitQueueAdmin(IConnection connection, HttpClient management) : IQueueAdmin
{
    public async Task DeclareQueueAsync(QueueDefinition definition, CancellationToken ct = default)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await MessagingTopology.DeclareQueueAsync(
            channel,
            definition.Name,
            definition.Exchange,
            definition.RoutingKey,
            definition.DeadLetter ? MessagingTopology.DomainEventsDlx : null,
            ct);
    }

    public async Task<IReadOnlyList<QueueInfo>> ListQueuesAsync(CancellationToken ct = default)
    {
        var queues = await management.GetFromJsonAsync<List<ManagementQueue>>("api/queues/%2F?columns=name,messages,consumers", ct) ?? [];
        return queues.Select(q => new QueueInfo(q.Name, q.Messages ?? 0, q.Consumers ?? 0)).OrderBy(q => q.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<uint> PurgeQueueAsync(string queue, CancellationToken ct = default)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        return await channel.QueuePurgeAsync(queue, ct);
    }

    public async Task DeleteQueueAsync(string queue, bool ifUnused = true, CancellationToken ct = default)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.QueueDeleteAsync(queue, ifUnused, ifEmpty: false, cancellationToken: ct);
    }

    private sealed record ManagementQueue(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("messages")] uint? Messages,
        [property: JsonPropertyName("consumers")] uint? Consumers);
}
