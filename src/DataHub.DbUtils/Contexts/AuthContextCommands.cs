using DataHub.Auth;
using DataHub.Auth.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataHub.DbUtils.Contexts;

public sealed class AuthContextCommands(AuthDbContext db, ILogger<AuthContextCommands> logger) : ContextCommands<AuthDbContext>(db, logger)
{
    public override string Name => "Auth";

    /// <summary>Roles and their permission claims from embedded <c>auth-roles.json</c>; permissions not listed are removed.</summary>
    public override async Task<int> SeedMasterDataAsync(CancellationToken ct)
    {
        var changed = 0;
        foreach (var seed in ReadMasterData<RolesSeed>("auth-roles.json").Roles)
        {
            var unknown = seed.Permissions.Except(Permissions.All, StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                throw new InvalidDataException($"auth-roles.json: unknown permissions {string.Join(", ", unknown)} for role {seed.Name}.");
            }

            var role = await Db.Roles.SingleOrDefaultAsync(r => r.Id == seed.Id, ct);
            if (role is null)
            {
                Db.Roles.Add(new AppRole { Id = seed.Id, Name = seed.Name, NormalizedName = seed.Name.ToUpperInvariant(), ConcurrencyStamp = Guid.NewGuid().ToString() });
                changed++;
            }
            else if (role.Name != seed.Name)
            {
                (role.Name, role.NormalizedName) = (seed.Name, seed.Name.ToUpperInvariant());
                changed++;
            }

            var existing = await Db.RoleClaims.Where(c => c.RoleId == seed.Id && c.ClaimType == Permissions.ClaimType).ToListAsync(ct);
            var stale = existing.Where(c => !seed.Permissions.Contains(c.ClaimValue!)).ToList();
            Db.RoleClaims.RemoveRange(stale);
            var missing = seed.Permissions.Where(p => existing.All(c => c.ClaimValue != p)).ToList();
            Db.RoleClaims.AddRange(missing.Select(p => new IdentityRoleClaim<Guid> { RoleId = seed.Id, ClaimType = Permissions.ClaimType, ClaimValue = p }));
            changed += stale.Count + missing.Count;
        }

        await Db.SaveChangesAsync(ct);
        return changed;
    }

    private sealed record RoleSeed(Guid Id, string Name, string[] Permissions);

    private sealed record RolesSeed(RoleSeed[] Roles);
}
