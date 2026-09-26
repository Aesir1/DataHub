using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using DataHub.IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

[assembly: AssemblyFixture(typeof(StackFixture))]

namespace DataHub.IntegrationTests;

/// <summary>
/// AS-8: boots the real AppHost once per test run (Docker, func and bun required), runs the explicit-start
/// <c>dbutils-seed</c> resource, and waits until the webhook and web front end answer.
/// </summary>
public sealed class StackFixture : IAsyncLifetime
{
    public DistributedApplication App { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.DataHub_AppHost>(ct);
        App = await builder.BuildAsync(ct);
        await App.StartAsync(ct);

        var notifications = App.ResourceNotifications;
        await notifications.WaitForResourceHealthyAsync("api", ct).WaitAsync(TimeSpan.FromMinutes(5), ct);
        await notifications.WaitForResourceHealthyAsync("webhook", ct).WaitAsync(TimeSpan.FromMinutes(5), ct);

        // DU-1/AS-4: seeding is destructive and explicit-start; start it like the dashboard button does.
        var commands = App.Services.GetRequiredService<ResourceCommandService>();
        var seed = App.Services.GetRequiredService<DistributedApplicationModel>().Resources.Single(r => r.Name == "dbutils-seed");
        var started = await commands.ExecuteCommandAsync(seed, "resource-start", ct);
        started.Success.ShouldBeTrue(started.Message);
        // Wait for this run to start (StartTimeStamp set) before waiting for it to end, so a stale snapshot
        // cannot release the tests while the seed is still dropping databases.
        var seeded = await notifications.WaitForResourceAsync(
                "dbutils-seed",
                e => e.Snapshot.StartTimeStamp is not null && e.Snapshot.StopTimeStamp is not null && e.Snapshot.ExitCode is not null,
                ct)
            .WaitAsync(TimeSpan.FromMinutes(5), ct);
        seeded.Snapshot.ExitCode.ShouldBe(0, "dbutils-seed failed; see its logs in the test output");

        // The in-process AppHost's health checks share this process' Npgsql pool; the reseed's
        // DROP DATABASE ... WITH (FORCE) killed those connections, so do not hand them to tests.
        NpgsqlConnection.ClearAllPools();

        await WaitForHttpAsync("web", "/", ct);
    }

    public async ValueTask DisposeAsync() => await App.DisposeAsync();

    public async Task<string> ParameterAsync(string name)
    {
        var model = App.Services.GetRequiredService<DistributedApplicationModel>();
        return (await model.Resources.OfType<ParameterResource>().Single(p => p.Name == name).GetValueAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task WaitForHttpAsync(string resource, string path, CancellationToken ct)
    {
        using var http = App.CreateHttpClient(resource);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(path, ct);
                if ((int)response.StatusCode < 500)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (watch.Elapsed < TimeSpan.FromMinutes(3))
            {
                // not listening yet
            }

            await Task.Delay(1000, ct);
        }
    }
}
