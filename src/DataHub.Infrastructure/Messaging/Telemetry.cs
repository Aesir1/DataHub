using System.Diagnostics;
using System.Text;
using RabbitMQ.Client;

namespace DataHub.Infrastructure.Messaging;

/// <summary>MQ-7: W3C trace context in message headers, so a mutation and its consumer share one trace.</summary>
public static class MessagingTelemetry
{
    public const string SourceName = "DataHub.Messaging";
    public static readonly ActivitySource Source = new(SourceName);

    public static void Inject(Activity? activity, IDictionary<string, object?> headers)
    {
        if (activity is null)
        {
            return;
        }

        DistributedContextPropagator.Current.Inject(activity, headers, static (carrier, key, value) =>
        {
            if (carrier is IDictionary<string, object?> h)
            {
                h[key] = value;
            }
        });
    }

    public static string? Header(IReadOnlyBasicProperties properties, string key) =>
        properties.Headers?.TryGetValue(key, out var value) == true
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string s => s,
                _ => null,
            }
            : null;
}
