using DataHub.Application.Abstractions;

namespace DataHub.Infrastructure.Persistence;

/// <summary>Caller for background work (consumers, DbUtils). The Auth project replaces it for HTTP requests.</summary>
public sealed class SystemCurrentUser : ICurrentUser
{
    public Guid? Id => null;

    public string? Email => null;

    public IReadOnlyCollection<string> Roles => [];

    public bool HasPermission(string permission) => false;
}
