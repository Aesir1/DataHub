using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace DataHub.AppHost;

/// <summary>
/// The Grafana observability stack (compose.observability.yaml), run by the AppHost:
/// Alloy (OTLP in, Docker logs, health probes) → Tempo / Loki / Prometheus → Grafana, plus cAdvisor
/// and postgres-exporter. Configuration lives in <c>docker/</c> at the repository root, shared with
/// <c>compose.observability.yaml</c>. The Aspire dashboard keeps
/// receiving telemetry too; the services export to both.
/// </summary>
/// <remarks>
/// DataHub isolates these containers on an <c>internal: true</c> network. Aspire puts every
/// container on its own network, so that isolation is not reproduced here; the Grafana settings that
/// stop it from calling the Internet are.
/// </remarks>
internal static class Observability
{
    public const string Project = "datahub";
    public const int GrafanaPort = 3001;
    public const int OtlpGrpcPort = 4317;
    public const int OtlpHttpPort = 4318;

    /// <summary>
    /// Labels a container like a compose service, so cAdvisor's label whitelist, Alloy's log discovery,
    /// and the dashboards and alert rules (which group by these two labels) treat it as part of DataHub.
    /// </summary>
    /// <remarks>
    /// Aspire only recreates a persistent container when its image or environment changes, not its (runtime)
    /// arguments. The environment variable makes adding the labels, or the extra postgres arguments, take effect
    /// on containers created before them; bump <see cref="StackRevision"/> for future argument-only changes.
    /// </remarks>
    public static IResourceBuilder<T> WithStackLabels<T>(this IResourceBuilder<T> container, string service)
        where T : ContainerResource =>
        container
            .WithContainerRuntimeArgs(
                "--label",
                $"com.docker.compose.project={Project}",
                "--label",
                $"com.docker.compose.service={service}")
            .WithEnvironment("DATAHUB_STACK", string.Create(CultureInfo.InvariantCulture, $"{Project}/{service}@{StackRevision}"));

    /// <summary>Part of every labelled container's environment; change it to force Aspire to recreate them.</summary>
    public const int StackRevision = 1;

    /// <summary>Memory cap with a matching GOMEMLIMIT (~85 %), and a low CPU weight so telemetry yields to the app.</summary>
    private static IResourceBuilder<ContainerResource> WithLimits(this IResourceBuilder<ContainerResource> container, int memoryMb, int goMemLimitMb) =>
        container
            .WithContainerRuntimeArgs($"--memory={memoryMb}m", "--cpu-shares=256")
            .WithEnvironment("GOMEMLIMIT", string.Create(CultureInfo.InvariantCulture, $"{goMemLimitMb}MiB"));

    private static IResourceBuilder<ContainerResource> Stack(this IDistributedApplicationBuilder builder, string name, string image, string tag) =>
        builder.AddContainer(name, image, tag)
            .WithLifetime(ContainerLifetime.Persistent)
            .WithStackLabels(name);

    public static ObservabilityStack AddObservability(this IDistributedApplicationBuilder builder, IResourceBuilder<PostgresServerResource> postgres, IResourceBuilder<PostgresDatabaseResource> appDb)
    {
        // Shared with compose.observability.yaml (production).
        var dir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "docker"));
        var grafanaPassword = builder.AddParameter("grafana-admin-password", secret: true);
        var exporterPassword = builder.AddParameter("pg-exporter-password", secret: true);
        var postgresPassword = postgres.Resource.PasswordParameter;

        var prometheus = builder.Stack("prometheus", "prom/prometheus", "v3.14.0")
            .WithLimits(192, 168)
            .WithArgs(
                "--config.file=/etc/prometheus/prometheus.yml",
                "--storage.tsdb.path=/prometheus",
                "--storage.tsdb.retention.time=30d",
                "--storage.tsdb.retention.size=2GB",
                "--web.enable-remote-write-receiver",
                "--enable-feature=exemplar-storage",
                "--web.listen-address=0.0.0.0:9090")
            .WithBindMount(Path.Combine(dir, "prometheus", "prometheus.yml"), "/etc/prometheus/prometheus.yml", isReadOnly: true)
            .WithVolume("datahub-prometheus", "/prometheus");

        var loki = builder.Stack("loki", "grafana/loki", "3.7.7")
            .WithLimits(128, 112)
            .WithArgs("-config.file=/etc/loki/loki.yaml")
            .WithBindMount(Path.Combine(dir, "loki", "loki.yaml"), "/etc/loki/loki.yaml", isReadOnly: true)
            .WithVolume("datahub-loki", "/loki");

        var tempo = builder.Stack("tempo", "grafana/tempo", "3.0.3")
            .WithLimits(176, 152)
            .WithArgs("-config.file=/etc/tempo/tempo.yaml")
            .WithBindMount(Path.Combine(dir, "tempo", "tempo.yaml"), "/etc/tempo/tempo.yaml", isReadOnly: true)
            .WithVolume("datahub-tempo", "/var/tempo");

        // The GF_* switches below keep Grafana from talking to the Internet.
        var grafana = builder.Stack("grafana", "grafana/grafana-oss", "13.0.2")
            .WithLimits(224, 192)
            .WithHttpEndpoint(port: GrafanaPort, targetPort: 3000, name: "http")
            .WithEndpointProxySupport(false)
            .WithEnvironment("GF_SECURITY_ADMIN_USER", "admin")
            .WithEnvironment("GF_SECURITY_ADMIN_PASSWORD", grafanaPassword)
            .WithEnvironment("GF_SERVER_ROOT_URL", string.Create(CultureInfo.InvariantCulture, $"http://localhost:{GrafanaPort}"))
            .WithEnvironment("GF_ANALYTICS_REPORTING_ENABLED", "false")
            .WithEnvironment("GF_ANALYTICS_CHECK_FOR_UPDATES", "false")
            .WithEnvironment("GF_ANALYTICS_CHECK_FOR_PLUGIN_UPDATES", "false")
            .WithEnvironment("GF_ANALYTICS_FEEDBACK_LINKS_ENABLED", "false")
            .WithEnvironment("GF_NEWS_NEWS_FEED_ENABLED", "false")
            .WithEnvironment("GF_SECURITY_DISABLE_GRAVATAR", "true")
            .WithEnvironment("GF_PLUGINS_PREINSTALL_DISABLED", "true")
            .WithEnvironment("GF_SNAPSHOTS_EXTERNAL_ENABLED", "false")
            .WithEnvironment("GF_SUPPORT_BUNDLES_ENABLED", "false")
            .WithEnvironment("GF_USERS_ALLOW_SIGN_UP", "false")
            .WithEnvironment("GF_AUTH_ANONYMOUS_ENABLED", "false")
            .WithEnvironment("GF_LOG_LEVEL", "warn")
            .WithEnvironment("GF_INSTALL_PLUGINS", string.Empty)
            .WithVolume("datahub-grafana", "/var/lib/grafana")
            .WithBindMount(Path.Combine(dir, "grafana", "provisioning"), "/etc/grafana/provisioning", isReadOnly: true)
            .WithBindMount(Path.Combine(dir, "grafana", "dashboards"), "/var/lib/grafana/dashboards", isReadOnly: true)
            .WithHttpHealthCheck("/api/health")
            .WaitFor(prometheus)
            .WaitFor(loki)
            .WaitFor(tempo);

        // Runs as root to read the Docker socket; only the OTLP ports are published, on loopback.
        var alloy = builder.Stack("alloy", "grafana/alloy", "v1.19.2")
            .WithLimits(224, 192)
            .WithArgs("run", "/etc/alloy/config.alloy", "--storage.path=/var/lib/alloy/data", "--server.http.listen-addr=0.0.0.0:12345", "--disable-reporting")
            .WithContainerRuntimeArgs("--user=root")
            .WithEndpoint(port: OtlpGrpcPort, targetPort: 4317, name: "otlp-grpc", scheme: "http")
            .WithHttpEndpoint(port: OtlpHttpPort, targetPort: 4318, name: "otlp-http")
            .WithEndpointProxySupport(false)
            .WithBindMount(Path.Combine(dir, "alloy", "config.alloy"), "/etc/alloy/config.alloy", isReadOnly: true)
            .WithBindMount("/var/run/docker.sock", "/var/run/docker.sock", isReadOnly: true)
            .WithBindMount("/", "/host/root", isReadOnly: true)
            .WithVolume("datahub-alloy", "/var/lib/alloy/data")
            .WaitFor(prometheus)
            .WaitFor(loki)
            .WaitFor(tempo);

        builder.Stack("cadvisor", "gcr.io/cadvisor/cadvisor", "v0.55.1")
            .WithContainerRuntimeArgs("--privileged", "--device=/dev/kmsg", "--cpus=0.25", "--memory=64m", "--cpu-shares=256")
            .WithEnvironment("GOMEMLIMIT", "56MiB")
            .WithArgs(
                "--docker_only=true",
                "--housekeeping_interval=30s",
                "--disable_metrics=advtcp,cpu_topology,cpuset,hugetlb,memory_numa,process,referenced_memory,resctrl,sched,tcp,udp,disk",
                "--store_container_labels=false",
                "--whitelisted_container_labels=com.docker.compose.project,com.docker.compose.service")
            .WithBindMount("/", "/rootfs", isReadOnly: true)
            .WithBindMount("/var/run", "/var/run", isReadOnly: true)
            .WithBindMount("/sys", "/sys", isReadOnly: true)
            .WithBindMount("/dev/disk/", "/dev/disk", isReadOnly: true);

        // Idempotent: (re)creates the read-only pg_monitor role and pg_stat_statements on every start.
        var pgMonitorInit = builder.Stack("pg-monitor-init", "postgres", "17-alpine")
            .WithEntrypoint("/bin/sh")
            .WithArgs("/scripts/provision.sh")
            .WithBindMount(Path.Combine(dir, "postgres-exporter"), "/scripts", isReadOnly: true)
            .WithEnvironment("POSTGRES_DB", appDb.Resource.DatabaseName)
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithEnvironment("PGPASSWORD", postgresPassword)
            .WithEnvironment("PG_EXPORTER_USER", "datahub_metrics")
            .WithEnvironment("PG_EXPORTER_PASSWORD", exporterPassword)
            .WaitFor(appDb);

        builder.Stack("postgres-exporter", "prometheuscommunity/postgres-exporter", "v0.20.1")
            .WithEnvironment("DATA_SOURCE_URI", $"postgres:5432/{appDb.Resource.DatabaseName}?sslmode=disable")
            .WithEnvironment("DATA_SOURCE_USER", "datahub_metrics")
            .WithEnvironment("DATA_SOURCE_PASS", exporterPassword)
            .WithArgs(
                "--collector.stat_statements",
                "--collector.stat_user_tables",
                "--collector.statio_user_tables",
                "--collector.statio_user_indexes",
                "--collector.long_running_transactions",
                "--collector.locks")
            .WaitForCompletion(pgMonitorInit);

        return new ObservabilityStack(alloy, grafana);
    }
}

internal sealed record ObservabilityStack(IResourceBuilder<ContainerResource> Alloy, IResourceBuilder<ContainerResource> Grafana)
{
    /// <summary>OTLP/HTTP endpoint of Alloy as seen from host processes.</summary>
    public EndpointReference OtlpHttp => Alloy.GetEndpoint("otlp-http");

    /// <summary>Health probe URL for Alloy, pointing at a host process through Aspire's container-to-host tunnel.</summary>
    public ObservabilityStack Probe<T>(string name, IResourceBuilder<T> resource, string endpoint, string path)
        where T : IResourceWithEndpoints
    {
        var url = resource.GetEndpoint(endpoint, KnownNetworkIdentifiers.DefaultAspireContainerNetwork);
        Alloy.WithEnvironment($"PROBE_{name.ToUpperInvariant()}", ReferenceExpression.Create($"{url}{path}"));
        return this;
    }
}
