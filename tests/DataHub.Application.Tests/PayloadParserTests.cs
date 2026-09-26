using System.Text.Json;
using DataHub.Application.Ingestion;
using DataHub.Domain.Messaging;

namespace DataHub.Application.Tests;

public class PayloadParserTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Parses_and_converts_every_reading()
    {
        var result = PayloadParser.Parse(Json("""
            {"containerId":" mscu1234567 ","readings":[
              {"timestamp":"2026-09-25T10:00:00Z","value":41,"unit":"F"},
              {"timestamp":"2026-09-25T12:00:00+02:00","value":278.15,"unit":"k"},
              {"timestamp":"2026-09-25T11:00:00Z","value":-2.5,"unit":"C"}]}
            """));

        result.ContainerCode.ShouldBe("MSCU1234567");
        result.Readings.Select(r => r.Celsius).ShouldBe([5m, 5m, -2.5m]);
        result.Readings[1].TimestampUtc.ShouldBe(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero));
        result.Readings[1].TimestampUtc.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"readings":[{"timestamp":"2026-09-25T10:00:00Z","value":1,"unit":"C"}]}""")]
    [InlineData("""{"containerId":"","readings":[{"timestamp":"2026-09-25T10:00:00Z","value":1,"unit":"C"}]}""")]
    [InlineData("""{"containerId":"A","readings":[]}""")]
    [InlineData("""{"containerId":"A","readings":{}}""")]
    [InlineData("""{"containerId":"A","readings":[{"timestamp":"yesterday","value":1,"unit":"C"}]}""")]
    [InlineData("""{"containerId":"A","readings":[{"timestamp":"2026-09-25T10:00:00Z","value":"1","unit":"C"}]}""")]
    [InlineData("""{"containerId":"A","readings":[{"timestamp":"2026-09-25T10:00:00Z","value":1,"unit":"R"}]}""")]
    [InlineData("""{"containerId":"A","readings":[{"timestamp":"2026-09-25T10:00:00Z","value":1,"unit":"5"}]}""")]
    [InlineData("""{"containerId":"A","readings":[{"timestamp":"2026-09-25T10:00:00Z","value":1}]}""")]
    [InlineData("""{"containerId":"A","readings":[42]}""")]
    public void Rejects_invalid_payloads(string json) =>
        Should.Throw<InvalidPayloadException>(() => PayloadParser.Parse(Json(json)));

    [Fact]
    public async Task Ingest_saves_under_message_id()
    {
        var store = Substitute.For<ITemperatureStore>();
        store.SaveAsync(Arg.Any<string>(), Arg.Any<ContainerReadings>(), Arg.Any<CancellationToken>()).Returns(true);
        var message = new WebhookReceived("msg-1", "container", "container/x.json", DateTimeOffset.UtcNow, Json("""
            {"containerId":"ABCU1234560","readings":[{"timestamp":"2026-09-25T10:00:00Z","value":50,"unit":"F"}]}
            """));

        (await new IngestTemperaturesService(store).IngestAsync(message, TestContext.Current.CancellationToken)).ShouldBeTrue();

        await store.Received(1).SaveAsync(
            "msg-1",
            Arg.Is<ContainerReadings>(r => r.ContainerCode == "ABCU1234560" && r.Readings.Single().Celsius == 10m),
            Arg.Any<CancellationToken>());
    }
}
