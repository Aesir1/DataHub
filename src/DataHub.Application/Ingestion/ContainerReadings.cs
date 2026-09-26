using DataHub.Domain.Containers;

namespace DataHub.Application.Ingestion;

public sealed record Reading(DateTimeOffset TimestampUtc, decimal Celsius);

public sealed record ContainerReadings(string ContainerCode, IReadOnlyList<Reading> Readings);

/// <summary>Payload cannot be processed; retrying will not help.</summary>
public sealed class InvalidPayloadException(string message) : DataHub.Domain.Exceptions.DomainException(message);

public interface ITemperatureStore
{
    /// <summary>Saves the readings once per <paramref name="messageId"/>. Returns false when the message was already processed.</summary>
    Task<bool> SaveAsync(string messageId, ContainerReadings readings, CancellationToken ct = default);
}
