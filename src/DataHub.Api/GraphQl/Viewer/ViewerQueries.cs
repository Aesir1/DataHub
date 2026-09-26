using DataHub.Application.Abstractions;
using DataHub.Auth;

namespace DataHub.Api.GraphQl.Viewer;

public sealed record Viewer(Guid? Id, string? Email, IReadOnlyCollection<string> Roles, IReadOnlyList<string> Permissions);

[ExtendObjectType(typeof(RootQuery))]
public class ViewerQueries : RootQuery
{
    /// <summary>The signed-in user, so the UI can show only what the caller may use.</summary>
#pragma warning disable S2325, CA1822 // HotChocolate only binds instance resolvers on type extensions.
    public Viewer GetViewer([Service] ICurrentUser user) =>
        new(user.Id, user.Email, user.Roles, Permissions.All.Where(user.HasPermission).ToList());
#pragma warning restore S2325, CA1822
}
