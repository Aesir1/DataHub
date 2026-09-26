using DataHub.Domain.Exceptions;

namespace DataHub.Api.GraphQl.Errors;

/// <summary>Typed mutation errors (GQ-3). Only the safe domain message is exposed, never raw exception text.</summary>
public interface IDomainError
{
    string Message { get; }

    string Code { get; }
}

public sealed record NotFoundError(string Message) : IDomainError
{
    public string Code => ErrorCodes.NotFound;

    public static NotFoundError CreateErrorFrom(NotFoundException ex) => new(ex.Message);
}

public sealed record ConflictError(string Message) : IDomainError
{
    public string Code => ErrorCodes.Conflict;

    public static ConflictError CreateErrorFrom(ConflictException ex) => new(ex.Message);
}

public sealed record ConcurrencyError(string Message) : IDomainError
{
    public string Code => ErrorCodes.Concurrency;

    public static ConcurrencyError CreateErrorFrom(ConcurrencyException ex) => new(ex.Message);
}

public sealed record ForbiddenError(string Message) : IDomainError
{
    public string Code => ErrorCodes.Forbidden;

    public static ForbiddenError CreateErrorFrom(ForbiddenException ex) => new(ex.Message);
}

public sealed record FieldError(string Field, IReadOnlyList<string> Messages);

public sealed record ValidationError(string Message, IReadOnlyList<FieldError> Fields) : IDomainError
{
    public string Code => ErrorCodes.Validation;

    public static ValidationError CreateErrorFrom(ValidationException ex) =>
        new("Validation failed.", ex.Errors.Select(e => new FieldError(e.Key, e.Value)).ToList());
}

public static class ErrorCodes
{
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string Concurrency = "CONCURRENCY";
    public const string Forbidden = "FORBIDDEN";
    public const string Validation = "VALIDATION";
    public const string Unexpected = "UNEXPECTED";

    public static string For(DomainException ex) => ex switch
    {
        NotFoundException => NotFound,
        ConflictException => Conflict,
        ConcurrencyException => Concurrency,
        ForbiddenException => Forbidden,
        ValidationException => Validation,
        _ => Unexpected,
    };
}
