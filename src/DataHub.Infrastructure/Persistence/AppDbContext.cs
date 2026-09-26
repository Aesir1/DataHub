using System.Linq.Expressions;
using DataHub.Domain.Abstractions;
using DataHub.Domain.Containers;
using DataHub.Domain.Documents;
using DataHub.Domain.Products;
using Microsoft.EntityFrameworkCore;

namespace DataHub.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Container> Containers => Set<Container>();

    public DbSet<Temperature> Temperatures => Set<Temperature>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductAudit> ProductAudits => Set<ProductAudit>();

    public DbSet<Document> Documents => Set<Document>();

    /// <summary>Keyless view defined in Persistence/Sql/ContainerSummaries.sql.</summary>
    public DbSet<ContainerSummary> ContainerSummaries => Set<ContainerSummary>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        foreach (var type in modelBuilder.Model.GetEntityTypes().Select(t => t.ClrType).ToList())
        {
            if (typeof(AuditableEntity).IsAssignableFrom(type))
            {
                modelBuilder.Entity(type).Property(nameof(AuditableEntity.Id)).ValueGeneratedNever();
                modelBuilder.Entity(type).Property(nameof(AuditableEntity.CreatedBy)).HasMaxLength(256);
                modelBuilder.Entity(type).Property(nameof(AuditableEntity.UpdatedBy)).HasMaxLength(256);
                modelBuilder.Entity(type).Property(nameof(AuditableEntity.RowVersion)).IsRowVersion();
            }

            if (typeof(ISoftDelete).IsAssignableFrom(type))
            {
                var e = Expression.Parameter(type, "e");
                var notDeleted = Expression.Lambda(Expression.Not(Expression.Property(e, nameof(ISoftDelete.IsDeleted))), e);
                modelBuilder.Entity(type).HasQueryFilter(notDeleted);
            }
        }
    }
}

/// <summary>Inbox record: one row per consumed RabbitMQ message-id, so redelivery is a no-op.</summary>
public class ProcessedMessage
{
    public required string Id { get; set; }

    public DateTimeOffset ProcessedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Domain event written in the same transaction as the change; published by <c>OutboxPublisher</c>.</summary>
public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Type { get; set; }

    public required string RoutingKey { get; set; }

    /// <summary>JSON body (jsonb).</summary>
    public required string Payload { get; set; }

    /// <summary>W3C traceparent of the request that wrote the change, so publish and consume join its trace.</summary>
    public string? TraceParent { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public DateTimeOffset? PublishedAtUtc { get; set; }

    public int Attempts { get; set; }
}
