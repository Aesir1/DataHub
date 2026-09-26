using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>Aspire service defaults: OpenTelemetry, health checks, service discovery and HTTP resilience.</summary>
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string ServiceNamespace = "datahub";

    /// <summary>Kept alive for the process lifetime: a collected Meter stops reporting.</summary>
    private static readonly Meter ProcessMeter = CreateProcessMeter();

    private static DateTimeOffset? ProcessStartedAt { get; set; }

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });
        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        ProcessStartedAt ??= DateTimeOffset.UtcNow;

        // service.namespace makes the Prometheus job "datahub/<service>", which the Grafana dashboards select on.
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddAttributes([new KeyValuePair<string, object>("service.namespace", ServiceNamespace)]))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(ProcessMeter.Name))
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName, "DataHub.Messaging")
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx =>
                    !ctx.Request.Path.StartsWithSegments(HealthEndpointPath, StringComparison.Ordinal)
                    && !ctx.Request.Path.StartsWithSegments(AlivenessEndpointPath, StringComparison.Ordinal))
                .AddHttpClientInstrumentation());

        // 1) The Aspire dashboard (OTEL_EXPORTER_OTLP_* injected by the AppHost).
        // 2) The Grafana stack: Alloy at OTEL_COLLECTOR_ENDPOINT, also injected by the AppHost.
        // Per-signal AddOtlpExporter instead of UseOtlpExporter, because the two cannot be combined. Each signal needs
        // its own options name: named OtlpExporterOptions are shared, so one name would give all three the same URL.
        var dashboard = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var collector = builder.Configuration["OTEL_COLLECTOR_ENDPOINT"]?.TrimEnd('/');
        if (dashboard)
        {
            otel.WithTracing(t => t.AddOtlpExporter())
                .WithMetrics(m => m.AddOtlpExporter())
                .WithLogging(l => l.AddOtlpExporter());
        }

        if (!string.IsNullOrWhiteSpace(collector))
        {
            otel.WithTracing(t => t.AddOtlpExporter("collector-traces", o => Collector(o, collector, "traces")))
                .WithMetrics(m => m.AddOtlpExporter("collector-metrics", (o, reader) =>
                {
                    Collector(o, collector, "metrics");
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 15_000;
                    reader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
                }))
                .WithLogging(l => l.AddOtlpExporter("collector-logs", o => Collector(o, collector, "logs")));
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        return builder;
    }

    private static Meter CreateProcessMeter()
    {
        // .NET's built-in meters have no uptime; the Grafana dashboards read process_uptime_seconds.
        var meter = new Meter("DataHub.Process");
        meter.CreateObservableGauge(
            "process.uptime",
            () => (DateTimeOffset.UtcNow - (ProcessStartedAt ?? DateTimeOffset.UtcNow)).TotalSeconds,
            unit: "s",
            description: "Seconds since the process started.");
        return meter;
    }

    private static void Collector(OtlpExporterOptions options, string endpoint, string signal)
    {
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.Endpoint = new Uri($"{endpoint}/v1/{signal}");
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(HealthEndpointPath);
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") });
        return app;
    }
}
