using DataHub.Application.Abstractions;
using DataHub.Auth;
using DataHub.Domain.Containers;
using DataHub.Infrastructure.Persistence;
using GreenDonut;
using HotChocolate.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DataHub.Api.GraphQl.Containers;

[ExtendObjectType(typeof(RootQuery))]
public class ContainerQueries : RootQuery
{
    [Authorize(Policy = Permissions.Containers.Read)]
    [UsePaging(MaxPageSize = 100, IncludeTotalCount = true)]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<Container> GetContainers([Service] IRepository<Container, Guid> containers) =>
        containers.Query().OrderBy(c => c.Code);

    /// <summary>Min/max/avg/latest reading of one container (SQL view ContainerSummaries).</summary>
    [Authorize(Policy = Permissions.Containers.Read)]
    public Task<ContainerSummary?> GetContainerSummary(Guid containerId, [Service] AppDbContext db, CancellationToken ct) =>
        db.ContainerSummaries.AsNoTracking().SingleOrDefaultAsync(s => s.ContainerId == containerId, ct);

    /// <summary>Readings of one container, oldest first.</summary>
    [Authorize(Policy = Permissions.Containers.Read)]
    [UsePaging(MaxPageSize = 100, DefaultPageSize = 100)]
    [UseProjection]
    public IQueryable<Temperature> GetTemperatures(Guid containerId, [Service] IRepository<Temperature, Guid> temperatures) =>
        temperatures.Query()
            .Where(t => t.ContainerId == containerId)
            .OrderBy(t => t.TimestampUtc)
            .ThenBy(t => t.Id);
}

public sealed class ContainerType : ObjectType<Container>
{
    protected override void Configure(IObjectTypeDescriptor<Container> descriptor)
    {
        descriptor.Ignore(c => c.Temperatures); // unbounded; use the paged temperatures(containerId) query
        descriptor.Field(c => c.RowVersion).Type<NonNullType<UnsignedIntType>>();
    }
}

public sealed class TemperatureType : ObjectType<Temperature>
{
    protected override void Configure(IObjectTypeDescriptor<Temperature> descriptor)
    {
        descriptor.Field(t => t.ContainerId).IsProjected(true);
        descriptor.Field(t => t.Container)
            .Resolve(ctx => ctx.DataLoader<IContainerByIdDataLoader>().LoadAsync(ctx.Parent<Temperature>().ContainerId, ctx.RequestAborted));
    }
}

public static class ContainerDataLoaders
{
    /// <summary>Batches Temperature.container lookups (GQ-4); own DI scope so it never shares a DbContext with a resolver.</summary>
    [DataLoader]
    public static async Task<IReadOnlyDictionary<Guid, Container>> GetContainerByIdAsync(
        IReadOnlyList<Guid> ids,
        IServiceScopeFactory scopes,
        CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRepository<Container, Guid>>().Query()
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);
    }
}
