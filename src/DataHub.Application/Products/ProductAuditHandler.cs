using DataHub.Application.Abstractions;
using DataHub.Domain.Products;

namespace DataHub.Application.Products;

/// <summary>Stores one audit row per <see cref="ProductChanged"/>; idempotent on message-id.</summary>
public sealed class ProductAuditHandler(IRepository<ProductAudit, Guid> audits) : IMessageHandler<ProductChanged>
{
    public async Task HandleAsync(ProductChanged message, MessageContext context, CancellationToken ct)
    {
        if ((await audits.ListAsync(a => a.MessageId == context.MessageId, ct)).Count > 0)
        {
            return;
        }

        await audits.AddAsync(
            new ProductAudit
            {
                ProductId = message.ProductId,
                Change = message.Change,
                Name = message.Name,
                Price = message.Price,
                MessageId = context.MessageId,
                OccurredAtUtc = message.OccurredAtUtc,
            },
            ct);
    }
}
