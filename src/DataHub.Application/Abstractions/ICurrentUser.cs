namespace DataHub.Application.Abstractions;

/// <summary>The only way Application code reads the caller. <see cref="Id"/> is null for background work.</summary>
public interface ICurrentUser
{
    Guid? Id { get; }

    string? Email { get; }

    IReadOnlyCollection<string> Roles { get; }

    bool IsAuthenticated => Id is not null;

    bool HasPermission(string permission);
}
