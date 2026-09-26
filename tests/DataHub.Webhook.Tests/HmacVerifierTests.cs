using System.Globalization;
using System.Text;

namespace DataHub.Webhook.Tests;

public class HmacVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"containerId":"MSCU1234567"}""");
    private static readonly string[] Secrets = ["current", "previous"];

    private static string Ts(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void Valid_signature_passes() =>
        HmacVerifier.Verify(Secrets, Ts(Now), HmacVerifier.Sign("current", Ts(Now), Body), Body, Now).ShouldBeTrue();

    [Fact]
    public void Second_active_secret_passes_during_rotation() =>
        HmacVerifier.Verify(Secrets, Ts(Now), HmacVerifier.Sign("previous", Ts(Now), Body), Body, Now).ShouldBeTrue();

    [Fact]
    public void Uppercase_hex_passes() =>
        HmacVerifier.Verify(Secrets, Ts(Now), HmacVerifier.Sign("current", Ts(Now), Body).ToUpperInvariant(), Body, Now).ShouldBeTrue();

    [Fact]
    public void Tampered_body_fails()
    {
        var signature = HmacVerifier.Sign("current", Ts(Now), Body);
        HmacVerifier.Verify(Secrets, Ts(Now), signature, Encoding.UTF8.GetBytes("""{"containerId":"XXXX1234567"}"""), Now).ShouldBeFalse();
    }

    [Fact]
    public void Wrong_secret_fails() =>
        HmacVerifier.Verify(Secrets, Ts(Now), HmacVerifier.Sign("guess", Ts(Now), Body), Body, Now).ShouldBeFalse();

    [Fact]
    public void Signature_bound_to_timestamp()
    {
        var signature = HmacVerifier.Sign("current", Ts(Now), Body);
        HmacVerifier.Verify(Secrets, Ts(Now.AddSeconds(1)), signature, Body, Now).ShouldBeFalse();
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void Stale_or_future_timestamp_fails(int offsetSeconds)
    {
        var at = Now.AddSeconds(offsetSeconds);
        HmacVerifier.Verify(Secrets, Ts(at), HmacVerifier.Sign("current", Ts(at), Body), Body, Now).ShouldBeFalse();
    }

    [Theory]
    [InlineData(300)]
    [InlineData(-300)]
    public void Timestamp_at_window_edge_passes(int offsetSeconds)
    {
        var at = Now.AddSeconds(offsetSeconds);
        HmacVerifier.Verify(Secrets, Ts(at), HmacVerifier.Sign("current", Ts(at), Body), Body, Now).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, "sha256=00")]
    [InlineData("", "sha256=00")]
    [InlineData("abc", "sha256=00")]
    [InlineData("-1", "sha256=00")]
    [InlineData("VALID", null)]
    [InlineData("VALID", "")]
    [InlineData("VALID", "md5=abcd")]
    [InlineData("VALID", "sha256=not-hex")]
    [InlineData("VALID", "sha256=abc")]
    public void Missing_or_malformed_headers_fail(string? timestamp, string? signature) =>
        HmacVerifier.Verify(Secrets, timestamp == "VALID" ? Ts(Now) : timestamp, signature, Body, Now).ShouldBeFalse();

    [Fact]
    public void No_configured_secret_fails() =>
        HmacVerifier.Verify([], Ts(Now), HmacVerifier.Sign("current", Ts(Now), Body), Body, Now).ShouldBeFalse();
}
