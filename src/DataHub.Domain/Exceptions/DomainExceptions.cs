namespace DataHub.Domain.Exceptions;

/// <summary>Base for expected failures. The message is safe to show to callers.</summary>
public abstract class DomainException(string message) : Exception(message);

public sealed class NotFoundException(string message) : DomainException(message)
{
    public static NotFoundException For<T>(object id) => new($"{typeof(T).Name} {id} was not found.");
}

public sealed class ConflictException(string message) : DomainException(message);

/// <summary>The row changed since it was read (xmin mismatch).</summary>
public sealed class ConcurrencyException(string message) : DomainException(message);

public sealed class ForbiddenException(string message = "You are not allowed to do this.") : DomainException(message);

public sealed class ValidationException(IReadOnlyDictionary<string, string[]> errors)
    : DomainException("Validation failed: " + string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(", ", e.Value)}")))
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}
