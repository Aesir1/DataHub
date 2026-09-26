using DataHub.Auth;
using DataHub.Domain.Exceptions;
using HotChocolate.Execution;

namespace DataHub.Api.GraphQl.Errors;

/// <summary>
/// GQ-6: domain exceptions surface as their safe message plus an error code; anything else is stripped to
/// "Unexpected Execution Error" unless in Development (IncludeExceptionDetails) or the caller is platform-admin.
/// </summary>
public sealed class ErrorFilter(IHttpContextAccessor http) : IErrorFilter
{
    public IError OnError(IError error) => error.Exception switch
    {
        DomainException ex => ErrorBuilder.FromError(error).SetMessage(ex.Message).SetCode(ErrorCodes.For(ex)).SetException(null).Build(),
        { } ex when http.HttpContext?.User.IsInRole(Roles.PlatformAdmin) == true => error.WithMessage(ex.Message),
        _ => error,
    };
}
