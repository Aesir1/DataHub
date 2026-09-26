using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DataHub.Webhook;

/// <summary>Verifies <c>X-Webhook-Signature: sha256=&lt;hex&gt;</c> = HMAC-SHA256(secret, "{timestamp}.{raw body}").</summary>
public static class HmacVerifier
{
    public const string TimestampHeader = "X-Webhook-Timestamp";
    public const string SignatureHeader = "X-Webhook-Signature";
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(300);

    private const string Prefix = "sha256=";

    public static bool Verify(IReadOnlyList<string> secrets, string? timestamp, string? signature, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (secrets.Count == 0
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds)
            || Math.Abs(now.ToUnixTimeSeconds() - unixSeconds) > Tolerance.TotalSeconds
            || signature is null
            || !signature.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(signature.AsSpan(Prefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var signed = new byte[timestamp.Length + 1 + body.Length];
        Encoding.ASCII.GetBytes(timestamp, signed);
        signed[timestamp.Length] = (byte)'.';
        body.CopyTo(signed.AsSpan(timestamp.Length + 1));

        var match = false;
        foreach (var secret in secrets)
        {
            // Check every secret so timing does not reveal which one matched.
            match |= CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed), expected);
        }

        return match;
    }

    public static string Sign(string secret, string timestamp, ReadOnlySpan<byte> body)
    {
        var signed = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body.ToArray()).ToArray();
        return Prefix + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed));
    }
}
