using System.Security.Claims;
using DataHub.Auth.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

namespace DataHub.Auth.Tests;

public sealed class AuthTests : IAsyncLifetime
{
    private static readonly Guid UserRole = Guid.CreateVersion7();

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero));
    private readonly MemoryCache cache = new(new MemoryCacheOptions());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync(Ct);
        await using var db = Db();
        await db.Database.MigrateAsync(Ct);
        db.Roles.Add(new AppRole { Id = UserRole, Name = Roles.User, NormalizedName = "USER" });
        db.RoleClaims.Add(new IdentityRoleClaim<Guid> { RoleId = UserRole, ClaimType = Permissions.ClaimType, ClaimValue = Permissions.Products.Read });
        await db.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        cache.Dispose();
        await postgres.DisposeAsync();
    }

    private AuthDbContext Db() => new(new DbContextOptionsBuilder<AuthDbContext>()
        .UseNpgsql(postgres.GetConnectionString(), o => o.MigrationsHistoryTable("__EFMigrationsHistory", AuthDbContext.Schema)).Options);

    private static ClaimsPrincipal Token(string sub, string? email, params string[] roles) => new(new ClaimsIdentity(
        [
            new Claim("sub", sub),
            .. email is null ? Array.Empty<Claim>() : [new Claim("email", email)],
            new Claim("realm_access", $$"""{"roles":[{{string.Join(',', roles.Select(r => $"\"{r}\""))}}]}"""),
        ],
        authenticationType: "Bearer"));

    private async Task<ClaimsPrincipal> Transform(ClaimsPrincipal principal)
    {
        await using var db = Db();
        return await new KeycloakClaimsTransformation(db, cache, time).TransformAsync(principal);
    }

    [Fact]
    public async Task First_request_provisions_user_with_keycloak_login_and_no_password()
    {
        var principal = await Transform(Token("kc-1", "one@local.test", Roles.User));

        await using var db = Db();
        var user = await db.Users.SingleAsync(Ct);
        user.Email.ShouldBe("one@local.test");
        user.PasswordHash.ShouldBeNull();
        (await db.UserLogins.SingleAsync(Ct)).ShouldSatisfyAllConditions(
            l => l.LoginProvider.ShouldBe("keycloak"),
            l => l.ProviderKey.ShouldBe("kc-1"),
            l => l.UserId.ShouldBe(user.Id));
        principal.FindFirst(DataHubClaims.UserId)!.Value.ShouldBe(user.Id.ToString());
    }

    [Fact]
    public async Task Maps_realm_roles_and_role_permissions()
    {
        var principal = await Transform(Token("kc-2", "two@local.test", Roles.User, "offline_access"));

        principal.IsInRole(Roles.User).ShouldBeTrue();
        principal.IsInRole("offline_access").ShouldBeTrue();
        principal.FindAll(Permissions.ClaimType).Select(c => c.Value).ShouldBe([Permissions.Products.Read]);
    }

    [Fact]
    public async Task User_claims_add_permissions_and_second_request_hits_cache()
    {
        await Transform(Token("kc-3", "three@local.test", Roles.User));
        await using (var db = Db())
        {
            var userId = (await db.UserLogins.SingleAsync(l => l.ProviderKey == "kc-3", Ct)).UserId;
            db.UserClaims.Add(new IdentityUserClaim<Guid> { UserId = userId, ClaimType = Permissions.ClaimType, ClaimValue = Permissions.Queues.Manage });
            await db.SaveChangesAsync(Ct);
        }

        // Cached for 60 s: the new claim is not visible yet.
        (await Transform(Token("kc-3", "three@local.test", Roles.User))).HasClaim(Permissions.ClaimType, Permissions.Queues.Manage).ShouldBeFalse();

        cache.Clear();
        (await Transform(Token("kc-3", "three@local.test", Roles.User))).HasClaim(Permissions.ClaimType, Permissions.Queues.Manage).ShouldBeTrue();
        await using var check = Db();
        (await check.Users.CountAsync(u => u.Email == "three@local.test", Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Parallel_first_requests_provision_one_user()
    {
        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Transform(Token("kc-race", "race@local.test", $"r{i}"))));

        await using var db = Db();
        (await db.UserLogins.CountAsync(l => l.ProviderKey == "kc-race", Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Anonymous_or_already_transformed_principals_are_untouched()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        (await Transform(anonymous)).Claims.ShouldBeEmpty();

        var done = Token("kc-4", null, Roles.User);
        ((ClaimsIdentity)done.Identity!).AddClaim(new Claim(DataHubClaims.UserId, Guid.Empty.ToString()));
        (await Transform(done)).FindAll(Permissions.ClaimType).ShouldBeEmpty();
    }

    [Fact]
    public void Realm_roles_are_parsed_sorted_and_tolerate_missing_claim()
    {
        KeycloakClaimsTransformation.RealmRoles((ClaimsIdentity)Token("s", null, "b", "a").Identity!).ShouldBe(["a", "b"]);
        KeycloakClaimsTransformation.RealmRoles(new ClaimsIdentity()).ShouldBeEmpty();
    }

    [Fact]
    public void Current_user_reads_claims_from_http_context()
    {
        var principal = Token("kc-5", "five@local.test", Roles.PlatformAdmin);
        var id = Guid.CreateVersion7();
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(DataHubClaims.UserId, id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Role, Roles.PlatformAdmin));
        identity.AddClaim(new Claim(Permissions.ClaimType, Permissions.Queues.Manage));
        var user = new CurrentUser(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } });

        (user.Id, user.Email).ShouldBe((id, "five@local.test"));
        user.Roles.ShouldBe([Roles.PlatformAdmin]);
        user.HasPermission(Permissions.Queues.Manage).ShouldBeTrue();
        user.HasPermission(Permissions.Products.Write).ShouldBeFalse();

        // HttpContextAccessor shares one AsyncLocal, so use a fake for "no request".
        var nobody = new CurrentUser(Substitute.For<IHttpContextAccessor>());
        nobody.Id.ShouldBeNull();
        nobody.Email.ShouldBeNull();
        nobody.Roles.ShouldBeEmpty();
        nobody.HasPermission("x").ShouldBeFalse();
    }

    [Fact]
    public async Task One_policy_per_permission_requires_the_permission_claim()
    {
        var services = new ServiceCollection().AddLogging().AddDataHubAuth(new ConfigurationBuilder().Build()).BuildServiceProvider();
        var policies = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var holder = new ClaimsPrincipal(new ClaimsIdentity([new Claim(Permissions.ClaimType, Permissions.Products.Write)], "Bearer"));

        foreach (var permission in Permissions.All)
        {
            (await policies.GetPolicyAsync(permission)).ShouldNotBeNull(permission);
        }

        (await authorization.AuthorizeAsync(holder, Permissions.Products.Write)).Succeeded.ShouldBeTrue();
        (await authorization.AuthorizeAsync(holder, Permissions.Queues.Manage)).Succeeded.ShouldBeFalse();
        Permissions.All.Count.ShouldBe(6);
    }
}
