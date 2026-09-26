using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DataHub.Auth.Persistence;

/// <summary>Local user store; no passwords (Keycloak owns credentials). The Keycloak <c>sub</c> is an external login.</summary>
public class AppUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset LastSeenAtUtc { get; set; }
}

/// <summary>Named like the Keycloak realm roles; its role claims carry the permissions.</summary>
public class AppRole : IdentityRole<Guid>;

public class AuthDbContext(DbContextOptions<AuthDbContext> options) : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public const string Schema = "auth";
    public const string KeycloakProvider = "keycloak";

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
        builder.Entity<AppUser>().Property(u => u.Id).ValueGeneratedNever();
        builder.Entity<AppRole>().Property(r => r.Id).ValueGeneratedNever();
    }
}
