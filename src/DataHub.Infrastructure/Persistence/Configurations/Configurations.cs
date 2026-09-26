using DataHub.Domain.Containers;
using DataHub.Domain.Documents;
using DataHub.Domain.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataHub.Infrastructure.Persistence.Configurations;

internal sealed class ContainerConfiguration : IEntityTypeConfiguration<Container>
{
    public void Configure(EntityTypeBuilder<Container> b)
    {
        b.Property(c => c.Code).HasMaxLength(64);
        b.HasIndex(c => c.Code).IsUnique();
        b.HasMany(c => c.Temperatures).WithOne(t => t.Container).HasForeignKey(t => t.ContainerId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TemperatureConfiguration : IEntityTypeConfiguration<Temperature>
{
    public void Configure(EntityTypeBuilder<Temperature> b)
    {
        b.Property(t => t.Id).ValueGeneratedNever();
        b.Property(t => t.Celsius).HasPrecision(8, 3);
        b.HasIndex(t => new { t.ContainerId, t.TimestampUtc });
    }
}

internal sealed class ContainerSummaryConfiguration : IEntityTypeConfiguration<ContainerSummary>
{
    public void Configure(EntityTypeBuilder<ContainerSummary> b) => b.HasNoKey().ToView("ContainerSummaries");
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.Property(p => p.Name).HasMaxLength(200);
        b.Property(p => p.Sku).HasMaxLength(32);
        b.Property(p => p.Price).HasPrecision(12, 2);
        b.HasIndex(p => p.Sku).IsUnique().HasFilter("\"IsDeleted\" = false");
        b.Ignore(p => p.EventName);
    }
}

internal sealed class ProductAuditConfiguration : IEntityTypeConfiguration<ProductAudit>
{
    public void Configure(EntityTypeBuilder<ProductAudit> b)
    {
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property(a => a.Name).HasMaxLength(200);
        b.Property(a => a.Price).HasPrecision(12, 2);
        b.Property(a => a.Change).HasConversion<string>().HasMaxLength(16);
        b.Property(a => a.MessageId).HasMaxLength(128);
        b.HasIndex(a => a.MessageId).IsUnique();
        b.HasIndex(a => new { a.ProductId, a.OccurredAtUtc });
    }
}

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> b)
    {
        b.Property(d => d.FileName).HasMaxLength(255);
        b.Property(d => d.ContentType).HasMaxLength(128);
        b.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
        b.HasIndex(d => d.OwnerId);
        b.Ignore(d => d.ObjectKey);
    }
}

internal sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> b) => b.Property(m => m.Id).HasMaxLength(128);
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.Property(m => m.Id).ValueGeneratedNever();
        b.Property(m => m.Type).HasMaxLength(128);
        b.Property(m => m.RoutingKey).HasMaxLength(128);
        b.Property(m => m.Payload).HasColumnType("jsonb");
        b.Property(m => m.TraceParent).HasMaxLength(64);
        b.HasIndex(m => m.OccurredAtUtc).HasFilter("\"PublishedAtUtc\" IS NULL");
    }
}
