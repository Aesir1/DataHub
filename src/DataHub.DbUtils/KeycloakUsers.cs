using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DataHub.Auth;
using Microsoft.Extensions.Logging;

namespace DataHub.DbUtils;

public sealed class SeedOptions
{
    public const string Section = "Seed";

    [Required]
    public string KeycloakAdminUser { get; set; } = "admin";

    [Required]
    public string KeycloakAdminPassword { get; set; } = string.Empty;

    [Required]
    public string AdminPassword { get; set; } = string.Empty;

    [Required]
    public string UserPassword { get; set; } = string.Empty;

    /// <summary>Keycloak login theme to set on the realm; empty keeps the current one.</summary>
    public string? LoginTheme { get; set; }
}

/// <summary>Creates the local dev users through the Keycloak Admin REST API. Existing users are left untouched.</summary>
internal sealed class KeycloakUsers(HttpClient http, SeedOptions options, ILogger<KeycloakUsers> logger)
{
    public const string Realm = "datahub";

    public async Task<int> SeedAsync(CancellationToken ct)
    {
        Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);

        using var token = await http.PostAsync(
            "realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = options.KeycloakAdminUser,
                ["password"] = options.KeycloakAdminPassword,
            }),
            ct);
        token.EnsureSuccessStatusCode();
        var accessToken = (await token.Content.ReadFromJsonAsync<JsonObject>(ct))!["access_token"]!.GetValue<string>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        await EnsureRealmThemeAsync(ct);
        await EnsureDefaultRoleAsync(Roles.User, ct);
        var created = 0;
        created += await EnsureUserAsync("admin@local.test", options.AdminPassword, [Roles.User, Roles.PlatformAdmin], ct) ? 1 : 0;
        created += await EnsureUserAsync("user@local.test", options.UserPassword, [Roles.User], ct) ? 1 : 0;
        return created;
    }

    /// <summary>
    /// Self-registered users get <paramref name="role"/>: it is added to the realm's default-roles composite
    /// (the realm import's defaultRole block is not applied by Keycloak).
    /// </summary>
    private async Task EnsureDefaultRoleAsync(string role, CancellationToken ct)
    {
        var composite = $"admin/realms/{Realm}/roles/default-roles-{Realm}/composites";
        var current = await http.GetFromJsonAsync<JsonArray>($"{composite}/realm", ct) ?? [];
        if (current.Any(r => r?["name"]?.GetValue<string>() == role))
        {
            return;
        }

        var representation = await http.GetFromJsonAsync<JsonObject>($"admin/realms/{Realm}/roles/{role}", ct);
        using var add = await http.PostAsJsonAsync(composite, new JsonArray(representation), ct);
        add.EnsureSuccessStatusCode();
        logger.LogInformation("Added {Role} to default-roles-{Realm}", role, Realm);
    }

    /// <summary>AU-5: the branded login theme (keycloak/themes/datahub, generated from web/config/branding.json).</summary>
    private async Task EnsureRealmThemeAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.LoginTheme))
        {
            return;
        }

        using var update = await http.PutAsJsonAsync($"admin/realms/{Realm}", new { loginTheme = options.LoginTheme }, ct);
        update.EnsureSuccessStatusCode();
    }

    private async Task<bool> EnsureUserAsync(string email, string password, string[] roles, CancellationToken ct)
    {
        var realm = $"admin/realms/{Realm}";
        var existing = await http.GetFromJsonAsync<JsonArray>($"{realm}/users?email={Uri.EscapeDataString(email)}&exact=true", ct);
        if (existing is { Count: > 0 })
        {
            logger.LogInformation("Keycloak user {Email} exists", email);
            return false;
        }

        using var create = await http.PostAsJsonAsync(
            $"{realm}/users",
            new
            {
                username = email,
                email,
                enabled = true,
                emailVerified = true,
                firstName = email.Split('@')[0],
                lastName = "Local",
                credentials = new[] { new { type = "password", value = password, temporary = false } },
            },
            ct);
        create.EnsureSuccessStatusCode();
        var userId = create.Headers.Location!.Segments[^1];

        var roleRepresentations = new JsonArray();
        foreach (var role in roles)
        {
            roleRepresentations.Add(await http.GetFromJsonAsync<JsonObject>($"{realm}/roles/{role}", ct));
        }

        using var map = await http.PostAsJsonAsync($"{realm}/users/{userId}/role-mappings/realm", roleRepresentations, ct);
        map.EnsureSuccessStatusCode();
        logger.LogInformation("Keycloak user {Email} created with roles {Roles}", email, string.Join(',', roles));
        return true;
    }
}
