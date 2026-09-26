using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DataHub.Webhook;

/// <summary>Validates Keycloak client-credentials tokens against the cached JWKS (RS256 only).</summary>
public sealed class BearerVerifier(IConfigurationManager<OpenIdConnectConfiguration> oidc, IOptions<WebhookOptions> options, TimeProvider time)
{
    private readonly JsonWebTokenHandler handler = new();

    public async Task<bool> VerifyAsync(string token, WebhookSender sender, CancellationToken ct)
    {
        var result = await ValidateAsync(token, ct);
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Keycloak may have rotated keys: refresh the JWKS once.
            oidc.RequestRefresh();
            result = await ValidateAsync(token, ct);
        }

        return result.IsValid
            && (sender.ClientId is null
                || (result.Claims.TryGetValue("azp", out var azp) && string.Equals(azp as string, sender.ClientId, StringComparison.Ordinal)));
    }

    private async Task<TokenValidationResult> ValidateAsync(string token, CancellationToken ct)
    {
        var config = await oidc.GetConfigurationAsync(ct);
        return await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = config.Issuer,
            ValidAudience = options.Value.Audience,
            IssuerSigningKeys = config.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            LifetimeValidator = (notBefore, expires, _, p) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return (notBefore is null || notBefore <= now + p.ClockSkew) && expires > now - p.ClockSkew;
            },
        });
    }
}
