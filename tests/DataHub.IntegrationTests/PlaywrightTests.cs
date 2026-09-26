using System.Diagnostics;
using Aspire.Hosting.Testing;

namespace DataHub.IntegrationTests;

/// <summary>TE-5: the Playwright suite in web/e2e runs against the stack this fixture started, with the seeded users.</summary>
public sealed class PlaywrightTests(StackFixture stack)
{
    [Fact]
    public async Task Playwright_suite_passes()
    {
        var ct = TestContext.Current.CancellationToken;
        var web = stack.App.GetEndpoint("web", "http");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "DataHub.sln")))
        {
            root = root.Parent ?? throw new DirectoryNotFoundException("DataHub.sln not found");
        }

        var start = new ProcessStartInfo("bun", "run test:e2e")
        {
            WorkingDirectory = Path.Combine(root.FullName, "web"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["E2E_BASE_URL"] = web.ToString().TrimEnd('/');
        start.Environment["CI"] = "true";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        process.ExitCode.ShouldBe(0, $"{await output}\n{await error}");
    }
}
