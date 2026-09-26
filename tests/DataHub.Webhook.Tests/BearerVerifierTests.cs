using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace DataHub.Webhook.Tests;

public class BearerVerifierTests
{
    private readonly TestSetup setup = new(WebhookSender.Bearer);

    private Task<bool> Verify(string token) => setup.Bearer.VerifyAsync(token, setup.Sender, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Valid_token_passes() => (await Verify(setup.Token())).ShouldBeTrue();

    [Fact]
    public async Task Expired_token_fails() => (await Verify(setup.Token(lifetime: TimeSpan.FromMinutes(-2)))).ShouldBeFalse();

    [Fact]
    public async Task Wrong_issuer_fails() => (await Verify(setup.Token(issuer: "http://evil.test/realms/datahub"))).ShouldBeFalse();

    [Fact]
    public async Task Wrong_audience_fails() => (await Verify(setup.Token(audience: "api"))).ShouldBeFalse();

    [Fact]
    public async Task Token_of_another_client_fails() => (await Verify(setup.Token(clientId: "someone-else"))).ShouldBeFalse();

    [Fact]
    public async Task Unknown_key_id_fails() =>
        (await Verify(setup.Token(key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "other" }))).ShouldBeFalse();

    [Fact]
    public async Task Symmetric_algorithm_fails() =>
        (await Verify(setup.Token(key: new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), algorithm: SecurityAlgorithms.HmacSha256))).ShouldBeFalse();

    [Fact]
    public async Task Alg_none_fails()
    {
        static string B64(string s) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(s));
        var unsigned = $"{B64("""{"alg":"none","typ":"JWT"}""")}.{B64($$"""{"iss":"{{TestSetup.Issuer}}","aud":"webhook","azp":"webhook-container","exp":4102444800}""")}.";

        (await Verify(unsigned)).ShouldBeFalse();
    }

    [Fact]
    public async Task Garbage_fails() => (await Verify("not-a-jwt")).ShouldBeFalse();
}
