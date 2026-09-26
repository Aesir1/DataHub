using System.Globalization;
using System.Text.Json;
using DataHub.Domain.Containers;

namespace DataHub.Application.Ingestion;

/// <summary>
/// Parses <c>{"containerId":"MSCU1234567","readings":[{"timestamp":"2026-09-25T10:00:00Z","value":41.0,"unit":"F"}]}</c>
/// and converts every reading to Celsius.
/// </summary>
public static class PayloadParser
{
    public static ContainerReadings Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidPayloadException("Payload must be a JSON object.");
        }

        var code = payload.TryGetProperty("containerId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()!.Trim()
            : string.Empty;
        if (code.Length is 0 or > 64)
        {
            throw new InvalidPayloadException("containerId is required (max 64 characters).");
        }

        if (!payload.TryGetProperty("readings", out var readings) || readings.ValueKind != JsonValueKind.Array || readings.GetArrayLength() == 0)
        {
            throw new InvalidPayloadException("readings must be a non-empty array.");
        }

        var result = new List<Reading>(readings.GetArrayLength());
        var index = 0;
        foreach (var r in readings.EnumerateArray())
        {
            result.Add(ParseReading(r, index++));
        }

        return new ContainerReadings(code.ToUpperInvariant(), result);
    }

    private static Reading ParseReading(JsonElement r, int index)
    {
        if (r.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidPayloadException($"readings[{index}] must be an object.");
        }

        if (!r.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            throw new InvalidPayloadException($"readings[{index}].timestamp must be an ISO 8601 date-time.");
        }

        if (!r.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var value))
        {
            throw new InvalidPayloadException($"readings[{index}].value must be a number.");
        }

        if (!r.TryGetProperty("unit", out var u) || u.ValueKind != JsonValueKind.String
            || !Enum.TryParse<TemperatureUnit>(u.GetString(), ignoreCase: true, out var unit) || !Enum.IsDefined(unit))
        {
            throw new InvalidPayloadException($"readings[{index}].unit must be one of C, F, K.");
        }

        return new Reading(timestamp.ToUniversalTime(), TemperatureConversion.ToCelsius(value, unit));
    }
}
