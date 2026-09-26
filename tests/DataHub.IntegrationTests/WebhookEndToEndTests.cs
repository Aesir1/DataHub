using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.Hosting.Testing;
using DataHub.Webhook;
using Npgsql;

namespace DataHub.IntegrationTests;

/// <summary>WH-24: each scheme through Azure Functions and RabbitMQ into the Api consumer; unsigned gets 401.</summary>
public sealed class WebhookEndToEndTests(StackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string Code, string Body) Payload(decimal fahrenheit)
    {
        var code = $"TEST{Random.Shared.Next(1_000_000, 9_999_999)}";
        return (code, $$"""{"containerId":"{{code}}","readings":[{"timestamp":"2026-09-25T10:00:00Z","value":{{fahrenheit.ToString(CultureInfo.InvariantCulture)}},"unit":"F"}]}""");
    }

    [Fact]
    public async Task Hmac_signed_webhook_lands_in_postgres_in_celsius()
    {
        var (code, body) = Payload(41);
        var secret = await stack.ParameterAsync("webhook-hmac-secret");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        using var http = stack.App.CreateHttpClient("webhook");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/webhooks/container") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(HmacVerifier.TimestampHeader, ts);
        request.Headers.Add(HmacVerifier.SignatureHeader, HmacVerifier.Sign(secret, ts, Encoding.UTF8.GetBytes(body)));
        using var response = await http.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await CelsiusAsync(code)).ShouldBe(5.000m);
    }

    [Fact]
    public async Task Bearer_webhook_with_keycloak_client_credentials_lands_in_postgres()
    {
        var (code, body) = Payload(50);
        using var keycloak = stack.App.CreateHttpClient("keycloak");
        using var tokenResponse = await keycloak.PostAsync(
            "realms/datahub/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "webhook-container",
                ["client_secret"] = await stack.ParameterAsync("webhook-client-secret"),
            }),
            Ct);
        tokenResponse.EnsureSuccessStatusCode();
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonObject>(Ct))!["access_token"]!.GetValue<string>();

        using var http = stack.App.CreateHttpClient("webhook");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/webhooks/container") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await CelsiusAsync(code)).ShouldBe(10.000m);
    }

    [Fact]
    public async Task Unsigned_webhook_is_401()
    {
        using var http = stack.App.CreateHttpClient("webhook");
        using var response = await http.PostAsync("api/webhooks/container", new StringContent("{}", Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<object?> CelsiusAsync(string code)
    {
        await using var db = new NpgsqlConnection(await stack.App.GetConnectionStringAsync("app", Ct));
        await db.OpenAsync(Ct);
        await using var query = new NpgsqlCommand(
            """SELECT t."Celsius" FROM "Temperatures" t JOIN "Containers" c ON c."Id" = t."ContainerId" WHERE c."Code" = @code""", db);
        query.Parameters.AddWithValue("code", code);

        for (var i = 0; i < 60; i++)
        {
            if (await query.ExecuteScalarAsync(Ct) is { } celsius)
            {
                return celsius;
            }

            await Task.Delay(500, Ct);
        }

        return null;
    }
}
