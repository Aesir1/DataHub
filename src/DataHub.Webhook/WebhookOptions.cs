using System.ComponentModel.DataAnnotations;

namespace DataHub.Webhook;

public sealed class WebhookOptions
{
    public const string Section = "Webhook";

    /// <summary>Keycloak realm URL, for example http://localhost:8080/realms/datahub.</summary>
    [Required]
    public string Authority { get; set; } = string.Empty;

    public string Audience { get; set; } = "webhook";

    /// <summary>
    /// Require HTTPS for the OpenID metadata/JWKS. Off where Keycloak is reached over a private network
    /// (production compose: http://keycloak:8080) or locally over plain HTTP.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    public Dictionary<string, WebhookSender> Senders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WebhookSender
{
    public const string Bearer = "bearer";
    public const string Hmac = "hmac";

    /// <summary>Allowed schemes: <c>bearer</c>, <c>hmac</c> or both.</summary>
    public string[] Schemes { get; set; } = [];

    /// <summary>HMAC shared secrets; two may be active during rotation.</summary>
    public string[] Secrets { get; set; } = [];

    /// <summary>Keycloak client id (<c>azp</c>) that may send bearer tokens for this sender.</summary>
    public string? ClientId { get; set; }

    public bool Allows(string scheme) => Schemes.Contains(scheme, StringComparer.OrdinalIgnoreCase);
}

public sealed class ObjectStorageOptions
{
    [Required]
    public string Endpoint { get; set; } = string.Empty;

    [Required]
    public string AccessKey { get; set; } = string.Empty;

    [Required]
    public string SecretKey { get; set; } = string.Empty;

    public string Bucket { get; set; } = "webhooks";
}
