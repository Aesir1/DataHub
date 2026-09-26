using DataHub.Domain.Abstractions;

namespace DataHub.Domain.Containers;

/// <summary>A shipping container identified by its ISO 6346 code (for example MSCU1234567).</summary>
public class Container : AuditableEntity
{
    public required string Code { get; set; }

    public List<Temperature> Temperatures { get; set; } = [];
}
