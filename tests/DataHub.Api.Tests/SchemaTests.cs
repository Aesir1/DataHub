using DataHub.Api.GraphQl;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace DataHub.Api.Tests;

/// <summary>
/// GQ-10/GQ-11: the committed web/schema.graphql is the snapshot. A schema change fails this test until
/// the schema is re-exported (and codegen rerun), so every change is visible in review.
/// </summary>
public class SchemaTests
{
    private static string Normalize(string sdl) => sdl.ReplaceLineEndings("\n").Trim();

    [Fact]
    public async Task Committed_schema_matches_api()
    {
        var schema = await new ServiceCollection().AddDataHubGraphQl(isDevelopment: false).BuildSchemaAsync(cancellationToken: TestContext.Current.CancellationToken);

        var committed = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "web", "schema.graphql"), TestContext.Current.CancellationToken);

        Normalize(schema.ToString()).ShouldBe(
            Normalize(committed),
            "Schema changed: run `dotnet run --project src/DataHub.Api -- schema export --output web/schema.graphql` and `bun run codegen` in web/.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DataHub.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("DataHub.sln not found above the test output.");
    }
}
