using DataHub.Application.Ingestion;
using DataHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataHub.Infrastructure.Tests;

public sealed class TemperatureStoreTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await db.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<bool> Save(string id, ContainerReadings readings)
    {
        await using var ctx = db.Create();
        return await new TemperatureStore(ctx).SaveAsync(id, readings, Ct);
    }

    [Fact]
    public async Task Creates_container_and_readings()
    {
        (await Save("m1", new ContainerReadings("MSCU1234567", [new Reading(T0, 5m), new Reading(T0.AddHours(1), -1.25m)]))).ShouldBeTrue();

        await using var ctx = db.Create();
        var container = await ctx.Containers.Include(c => c.Temperatures).SingleAsync(Ct);
        container.Code.ShouldBe("MSCU1234567");
        container.CreatedBy.ShouldBe("system");
        container.Temperatures.OrderBy(t => t.TimestampUtc).Select(t => t.Celsius).ShouldBe([5m, -1.25m]);
        (await ctx.ProcessedMessages.SingleAsync(Ct)).Id.ShouldBe("m1");
    }

    [Fact]
    public async Task Appends_to_existing_container()
    {
        await Save("m1", new ContainerReadings("MSCU1234567", [new Reading(T0, 5m)]));
        await Save("m2", new ContainerReadings("MSCU1234567", [new Reading(T0.AddHours(1), 6m)]));

        await using var ctx = db.Create();
        (await ctx.Containers.CountAsync(Ct)).ShouldBe(1);
        (await ctx.Temperatures.CountAsync(Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Duplicate_message_id_is_ignored()
    {
        var readings = new ContainerReadings("MSCU1234567", [new Reading(T0, 5m)]);
        (await Save("m1", readings)).ShouldBeTrue();
        (await Save("m1", readings)).ShouldBeFalse();

        await using var ctx = db.Create();
        (await ctx.Temperatures.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_first_messages_for_same_container_both_land()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Save($"m{i}", new ContainerReadings("MSCU1234567", [new Reading(T0.AddMinutes(i), i)]))));

        await using var ctx = db.Create();
        (await ctx.Containers.CountAsync(Ct)).ShouldBe(1);
        (await ctx.Temperatures.CountAsync(Ct)).ShouldBe(8);
    }

    [Fact]
    public async Task Summary_view_aggregates_readings()
    {
        await Save("m1", new ContainerReadings("MSCU1234567", [new Reading(T0, 5m), new Reading(T0.AddHours(2), 1m), new Reading(T0.AddHours(1), 9m)]));

        await using var ctx = db.Create();
        var summary = await ctx.ContainerSummaries.SingleAsync(Ct);
        (summary.Readings, summary.MinCelsius, summary.MaxCelsius, summary.AvgCelsius, summary.LastCelsius).ShouldBe((3, 1m, 9m, 5m, 1m));
        summary.LastTimestampUtc.ShouldBe(T0.AddHours(2));
    }

    [Fact]
    public async Task Model_has_no_pending_changes()
    {
        await using var ctx = db.Create();
        ctx.Database.HasPendingModelChanges().ShouldBeFalse();
    }
}
