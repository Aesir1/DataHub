using DataHub.Application.Abstractions;
using DataHub.Auth;
using HotChocolate.Authorization;

namespace DataHub.Api.GraphQl.Messaging;

/// <summary>MQ-5: queue administration for platform admins (permission queues.manage).</summary>
[ExtendObjectType(typeof(RootQuery))]
public class QueueQueries : RootQuery
{
    [Authorize(Policy = Permissions.Queues.Manage)]
    public Task<IReadOnlyList<QueueInfo>> GetQueues([Service] IQueueAdmin admin, CancellationToken ct) => admin.ListQueuesAsync(ct);
}

[ExtendObjectType(typeof(RootMutation))]
public class QueueMutations : RootMutation
{
    /// <summary>Declares a durable queue bound to <c>domain-events</c> with its own dead-letter queue.</summary>
    [Authorize(Policy = Permissions.Queues.Manage)]
    public async Task<string> DeclareQueue(string name, string routingKey, [Service] IQueueAdmin admin, CancellationToken ct)
    {
        await admin.DeclareQueueAsync(new QueueDefinition(name, "domain-events", routingKey), ct);
        return name;
    }

    /// <summary>Removes every ready message; returns how many.</summary>
    [Authorize(Policy = Permissions.Queues.Manage)]
    [GraphQLType<NonNullType<UnsignedIntType>>]
    public Task<uint> PurgeQueue(string name, [Service] IQueueAdmin admin, CancellationToken ct) => admin.PurgeQueueAsync(name, ct);

    [Authorize(Policy = Permissions.Queues.Manage)]
    public async Task<string> DeleteQueue(string name, bool ifUnused, [Service] IQueueAdmin admin, CancellationToken ct)
    {
        await admin.DeleteQueueAsync(name, ifUnused, ct);
        return name;
    }
}

public sealed class QueueInfoType : ObjectType<QueueInfo>
{
    protected override void Configure(IObjectTypeDescriptor<QueueInfo> descriptor)
    {
        descriptor.Field(q => q.Messages).Type<NonNullType<UnsignedIntType>>();
        descriptor.Field(q => q.Consumers).Type<NonNullType<UnsignedIntType>>();
    }
}
