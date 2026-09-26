using System.Text;
using DataHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataHub.Infrastructure.Messaging;

/// <summary>
/// Publishes committed <see cref="OutboxMessage"/> rows to <c>domain-events</c>. While the broker is down rows
/// stay unpublished and are retried, so no committed event is lost (MQ-4). Delivery is at-least-once: the
/// outbox id is the message-id and consumers dedupe on it.
/// </summary>
public sealed class OutboxPublisher(IServiceScopeFactory scopes, RabbitMessagePublisher publisher, TimeProvider time, ILogger<OutboxPublisher> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await PublishBatchAsync(stoppingToken) > 0)
                {
                    // drain
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Outbox publish failed; retrying in {Interval}", Interval);
            }

            await Task.Delay(Interval, time, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>Publishes up to 50 pending messages in order; returns how many were published.</summary>
    public async Task<int> PublishBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // ponytail: single publisher assumed; add FOR UPDATE SKIP LOCKED when running several Api replicas.
        var batch = await db.OutboxMessages.Where(m => m.PublishedAtUtc == null).OrderBy(m => m.OccurredAtUtc).Take(50).ToListAsync(ct);
        var published = 0;
        foreach (var message in batch)
        {
            try
            {
                await publisher.PublishJsonAsync(
                    MessagingTopology.DomainEvents,
                    message.RoutingKey,
                    message.Type,
                    message.Id.ToString(),
                    Encoding.UTF8.GetBytes(message.Payload),
                    message.TraceParent,
                    ct);
                message.PublishedAtUtc = time.GetUtcNow();
                published++;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                message.Attempts++;
                await db.SaveChangesAsync(ct);
                throw;
            }
        }

        await db.SaveChangesAsync(ct);
        return published;
    }
}
