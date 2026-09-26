using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DataHub.Webhook.Tests;

/// <summary>A local "Keycloak": RSA signing key, issuer and a webhook config with one sender.</summary>
internal sealed class TestSetup
{
    public const string Issuer = "http://keycloak.test/realms/datahub";
    public const string Secret = "current-secret";
    public const string OldSecret = "previous-secret";

    public TestSetup(params string[] schemes)
    {
        Options = Microsoft.Extensions.Options.Options.Create(new WebhookOptions
        {
            Authority = Issuer,
            Senders =
            {
                ["container"] = new WebhookSender { Schemes = schemes, Secrets = [Secret, OldSecret], ClientId = "webhook-container" },
            },
        });
        var config = new OpenIdConnectConfiguration { Issuer = Issuer };
        config.SigningKeys.Add(Key);
        Oidc = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
        Bearer = new BearerVerifier(Oidc, Options, Time);
        Authenticator = new WebhookAuthenticator(Options, Bearer, Time);
    }

    public static RsaSecurityKey Key { get; } = new(RSA.Create(2048)) { KeyId = "test-key" };

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero));

    public IOptions<WebhookOptions> Options { get; }

    public IConfigurationManager<OpenIdConnectConfiguration> Oidc { get; }

    public BearerVerifier Bearer { get; }

    public WebhookAuthenticator Authenticator { get; }

    public WebhookSender Sender => Options.Value.Senders["container"];

    public string Token(
        string issuer = Issuer,
        string audience = "webhook",
        string clientId = "webhook-container",
        TimeSpan? lifetime = null,
        SecurityKey? key = null,
        string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var expires = now + (lifetime ?? TimeSpan.FromMinutes(5));
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = expires < now ? expires.AddMinutes(-5) : now,
            NotBefore = expires < now ? expires.AddMinutes(-5) : now,
            Expires = expires,
            Claims = new Dictionary<string, object> { ["azp"] = clientId },
            SigningCredentials = new SigningCredentials(key ?? Key, algorithm),
        });
    }
}
