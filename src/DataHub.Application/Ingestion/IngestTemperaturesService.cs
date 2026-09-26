using DataHub.Domain.Messaging;

namespace DataHub.Application.Ingestion;

public sealed class IngestTemperaturesService(ITemperatureStore store)
{
    public Task<bool> IngestAsync(WebhookReceived message, CancellationToken ct = default) =>
        store.SaveAsync(message.Id, PayloadParser.Parse(message.Payload), ct);
}
