using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace DataHub.Webhook;

/// <summary>Only gate in front of the function: Bearer or HMAC, as allowed per sender. Callers never learn which check failed.</summary>
public sealed class WebhookAuthenticator(IOptions<WebhookOptions> options, BearerVerifier bearer, TimeProvider time)
{
    public async Task<bool> IsAuthorizedAsync(string sender, IHeaderDictionary headers, byte[] body, CancellationToken ct)
    {
        if (!options.Value.Senders.TryGetValue(sender, out var config))
        {
            return false;
        }

        var authorization = headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return config.Allows(WebhookSender.Bearer) && await bearer.VerifyAsync(authorization["Bearer ".Length..].Trim(), config, ct);
        }

        if (headers.TryGetValue(HmacVerifier.SignatureHeader, out var signature))
        {
            return config.Allows(WebhookSender.Hmac) && HmacVerifier.Verify(
                config.Secrets,
                headers[HmacVerifier.TimestampHeader].ToString(),
                signature.ToString(),
                body,
                time.GetUtcNow());
        }

        return false;
    }
}
