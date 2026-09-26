using System.Security.Claims;
using DataHub.Application.Abstractions;
using Microsoft.AspNetCore.Http;

namespace DataHub.Auth;

/// <summary>AU-14: the HTTP caller as seen by Application code; <see cref="ClaimsPrincipal"/> never leaves the Api.</summary>
public sealed class CurrentUser(IHttpContextAccessor http) : ICurrentUser
{
    private ClaimsPrincipal? Principal => http.HttpContext?.User is { Identity.IsAuthenticated: true } user ? user : null;

    public Guid? Id => Guid.TryParse(Principal?.FindFirst(DataHubClaims.UserId)?.Value, out var id) ? id : null;

    public string? Email => Principal?.FindFirst(DataHubClaims.Email)?.Value;

#pragma warning disable S2365 // ICurrentUser exposes roles as a property; the claim set is tiny.
    public IReadOnlyCollection<string> Roles => Principal?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList() ?? [];
#pragma warning restore S2365

    public bool HasPermission(string permission) => Principal?.HasClaim(Permissions.ClaimType, permission) == true;
}
