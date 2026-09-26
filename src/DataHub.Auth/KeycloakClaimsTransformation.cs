using System.Security.Claims;
using System.Text.Json;
using DataHub.Auth.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace DataHub.Auth;

/// <summary>
/// AU-11/AU-12: maps Keycloak <c>realm_access.roles</c> to <see cref="ClaimTypes.Role"/>, provisions the local
/// user just in time (Keycloak <c>sub</c> stored as external login) and adds permission claims from the role
/// and user claims in <see cref="AuthDbContext"/>. The lookup is cached per user for 60 seconds.
/// </summary>
public sealed class KeycloakClaimsTransformation(AuthDbContext db, IMemoryCache cache, TimeProvider time) : IClaimsTransformation
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity
            || identity.HasClaim(c => c.Type == DataHubClaims.UserId)
            || identity.FindFirst(DataHubClaims.Subject)?.Value is not { } subject)
        {
            return principal;
        }

        var roles = RealmRoles(identity);
        foreach (var role in roles.Where(r => !identity.HasClaim(ClaimTypes.Role, r)))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        var email = identity.FindFirst(DataHubClaims.Email)?.Value;
        var local = await cache.GetOrCreateAsync(
            $"datahub:auth:{subject}:{string.Join(',', roles)}",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheFor;
                var userId = await ProvisionAsync(subject, email);
                return (UserId: userId, Permissions: await PermissionsAsync(userId, roles));
            });

        identity.AddClaim(new Claim(DataHubClaims.UserId, local.UserId.ToString()));
        foreach (var permission in local.Permissions)
        {
            identity.AddClaim(new Claim(Permissions.ClaimType, permission));
        }

        return principal;
    }

    public static IReadOnlyList<string> RealmRoles(ClaimsIdentity identity)
    {
        if (identity.FindFirst("realm_access")?.Value is not { } realmAccess)
        {
            return [];
        }

        using var doc = JsonDocument.Parse(realmAccess);
        return doc.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
            ? roles.EnumerateArray().Select(r => r.GetString()).OfType<string>().Order(StringComparer.Ordinal).ToList()
            : [];
    }

    private async Task<Guid> ProvisionAsync(string subject, string? email)
    {
        var login = await db.UserLogins.AsNoTracking()
            .SingleOrDefaultAsync(l => l.LoginProvider == AuthDbContext.KeycloakProvider && l.ProviderKey == subject);
        var now = time.GetUtcNow();
        if (login is not null)
        {
            await db.Users.Where(u => u.Id == login.UserId).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.LastSeenAtUtc, now)
                .SetProperty(u => u.Email, u => email ?? u.Email)
                .SetProperty(u => u.NormalizedEmail, u => email != null ? email.ToUpperInvariant() : u.NormalizedEmail));
            return login.UserId;
        }

        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email ?? subject,
            NormalizedUserName = (email ?? subject).ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email?.ToUpperInvariant(),
            EmailConfirmed = email is not null,
            SecurityStamp = Guid.NewGuid().ToString(),
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
        };
        db.Users.Add(user);
        db.UserLogins.Add(new IdentityUserLogin<Guid>
        {
            LoginProvider = AuthDbContext.KeycloakProvider,
            ProviderKey = subject,
            ProviderDisplayName = "Keycloak",
            UserId = user.Id,
        });

        try
        {
            await db.SaveChangesAsync();
            return user.Id;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A parallel first request provisioned the same user.
            db.ChangeTracker.Clear();
            return (await db.UserLogins.AsNoTracking().SingleAsync(l => l.LoginProvider == AuthDbContext.KeycloakProvider && l.ProviderKey == subject)).UserId;
        }
    }

    private async Task<IReadOnlyList<string>> PermissionsAsync(Guid userId, IReadOnlyList<string> roles)
    {
        var normalized = roles.Select(r => r.ToUpperInvariant()).ToList();
        var fromRoles = db.RoleClaims
            .Where(c => c.ClaimType == Permissions.ClaimType && db.Roles.Any(r => r.Id == c.RoleId && normalized.Contains(r.NormalizedName!)))
            .Select(c => c.ClaimValue!);
        var fromUser = db.UserClaims.Where(c => c.UserId == userId && c.ClaimType == Permissions.ClaimType).Select(c => c.ClaimValue!);
        return await fromRoles.Union(fromUser).ToListAsync();
    }
}
