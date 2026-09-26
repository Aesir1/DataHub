using System.Globalization;
using System.Text;
using DataHub.Domain.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataHub.Webhook.Tests;

public class ReceiveWebhookTests
{
    private const string Body = """{"containerId":"MSCU1234567","readings":[{"timestamp":"2026-09-25T10:00:00Z","value":41,"unit":"F"}]}""";

    private readonly TestSetup setup = new(WebhookSender.Hmac, WebhookSender.Bearer);
    private readonly IPayloadStore store = Substitute.For<IPayloadStore>();
    private readonly IWebhookPublisher publisher = Substitute.For<IWebhookPublisher>();

    public ReceiveWebhookTests() =>
        store.PutAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns("container/20260925T100000Z.json");

    private ReceiveWebhook Function(TestSetup s) => new(s.Authenticator, store, publisher, s.Time, NullLogger<ReceiveWebhook>.Instance);

    private Task<IActionResult> Run(HttpRequest request, TestSetup? s = null) =>
        Function(s ?? setup).Run(request, "container", TestContext.Current.CancellationToken);

    private static HttpRequest Request(string body, Action<IHeaderDictionary>? headers = null)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        headers?.Invoke(context.Request.Headers);
        return context.Request;
    }

    private HttpRequest Signed(string body, string secret = TestSetup.Secret) => Request(body, h =>
    {
        var ts = setup.Time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        h[HmacVerifier.TimestampHeader] = ts;
        h[HmacVerifier.SignatureHeader] = HmacVerifier.Sign(secret, ts, Encoding.UTF8.GetBytes(body));
    });

    [Fact]
    public async Task Hmac_request_is_stored_published_and_accepted()
    {
        var request = Signed(Body);
        request.Headers[ReceiveWebhook.IdHeader] = "hook-1";

        var result = await Run(request);

        result.ShouldBeOfType<AcceptedResult>();
        await store.Received(1).PutAsync("container", Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b) == Body), Arg.Any<CancellationToken>());
        await publisher.Received(1).PublishAsync(
            Arg.Is<WebhookReceived>(m => m.Id == "hook-1" && m.Sender == "container" && m.ObjectKey == "container/20260925T100000Z.json"
                && m.Payload.GetProperty("containerId").GetString() == "MSCU1234567"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Bearer_request_is_accepted()
    {
        var result = await Run(Request(Body, h => h.Authorization = $"Bearer {setup.Token()}"));

        result.ShouldBeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task Missing_id_gets_generated()
    {
        await Run(Signed(Body));

        await publisher.Received(1).PublishAsync(Arg.Is<WebhookReceived>(m => Guid.Parse(m.Id).Version == 7), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unsigned_request_is_401()
    {
        (await Run(Request(Body))).ShouldBeOfType<UnauthorizedResult>();
        await store.DidNotReceive().PutAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Wrong_secret_is_401() => (await Run(Signed(Body, "guess"))).ShouldBeOfType<UnauthorizedResult>();

    [Fact]
    public async Task Unknown_sender_is_401() =>
        (await Function(setup).Run(Signed(Body), "someone", TestContext.Current.CancellationToken)).ShouldBeOfType<UnauthorizedResult>();

    [Fact]
    public async Task Scheme_not_allowed_for_sender_is_401()
    {
        var hmacOnly = new TestSetup(WebhookSender.Hmac);

        (await Run(Request(Body, h => h.Authorization = $"Bearer {hmacOnly.Token()}"), hmacOnly)).ShouldBeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Event_header_becomes_routing_key_segment()
    {
        var request = Signed(Body);
        request.Headers[ReceiveWebhook.EventHeader] = "Alarm";

        (await Run(request)).ShouldBeOfType<AcceptedResult>();
        await publisher.Received(1).PublishAsync(Arg.Is<WebhookReceived>(m => m.Event == "alarm"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("dots.not.allowed")]
    [InlineData("#")]
    public async Task Invalid_event_header_is_400(string header)
    {
        var request = Signed(Body);
        request.Headers[ReceiveWebhook.EventHeader] = header;

        (await Run(request)).ShouldBeOfType<BadRequestObjectResult>();
        await store.DidNotReceive().PutAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Malformed_json_is_400() =>
        (await Run(Signed("{not json"))).ShouldBeOfType<BadRequestObjectResult>();

    [Fact]
    public async Task Body_over_1mb_is_413()
    {
        var result = await Run(Signed(new string(' ', ReceiveWebhook.MaxBodyBytes + 1)));

        result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task Body_over_1mb_without_content_length_is_413()
    {
        var request = Signed(new string(' ', ReceiveWebhook.MaxBodyBytes + 1));
        request.ContentLength = null;

        (await Run(request)).ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task Broker_failure_is_503()
    {
        publisher.PublishAsync(Arg.Any<WebhookReceived>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new RabbitMQ.Client.Exceptions.PublishException(1, isReturn: true)));

        (await Run(Signed(Body))).ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }
}
