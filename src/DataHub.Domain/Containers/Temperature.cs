using DataHub.Domain.Abstractions;

namespace DataHub.Domain.Containers;

/// <summary>One temperature reading, always stored in degrees Celsius.</summary>
public class Temperature : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ContainerId { get; set; }

    public Container? Container { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }

    public decimal Celsius { get; set; }
}

/// <summary>Read model backed by the <c>ContainerSummaries</c> SQL view.</summary>
public class ContainerSummary
{
    public Guid ContainerId { get; set; }

    public int Readings { get; set; }

    public decimal? MinCelsius { get; set; }

    public decimal? MaxCelsius { get; set; }

    public decimal? AvgCelsius { get; set; }

    public decimal? LastCelsius { get; set; }

    public DateTimeOffset? LastTimestampUtc { get; set; }
}
