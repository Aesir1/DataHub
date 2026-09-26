using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DataHub.Webhook;

/// <summary>AS-5 for the Functions host: <c>/api/health</c> (all checks) and <c>/api/alive</c> (liveness only).</summary>
public sealed class Health(HealthCheckService health)
{
    [Function("Health")]
    public async Task<IActionResult> Ready([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req, CancellationToken ct) =>
        Result(await health.CheckHealthAsync(ct));

    [Function("Alive")]
    public async Task<IActionResult> Alive([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "alive")] HttpRequest req, CancellationToken ct) =>
        Result(await health.CheckHealthAsync(r => r.Tags.Contains("live"), ct));

    private static StatusCodeResult Result(HealthReport report) =>
        new(report.Status == HealthStatus.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
}
