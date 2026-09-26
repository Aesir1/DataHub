using DataHub.Api.GraphQl.Errors;
using HotChocolate.Execution.Configuration;

namespace DataHub.Api.GraphQl;

public static class GraphQlSetup
{
    public static IRequestExecutorBuilder AddDataHubGraphQl(this IServiceCollection services, bool isDevelopment)
    {
        services.AddHttpContextAccessor();

        // GQ-1/GQ-4: AddDataHubApi() is source-generated and registers every [ExtendObjectType] feature class,
        // ObjectType and [DataLoader] in this assembly, so adding a feature never touches Program.cs.
        var graphql = services.AddGraphQLServer()
            .AddAuthorization()
            .AddQueryType<RootQuery>()
            .AddMutationType<RootMutation>()
            .AddMutationConventions(applyToAllMutations: true)
            .AddProjections()
            .AddFiltering()
            .AddSorting()
            .AddDataHubApi()
            .AddApplicationService<IHttpContextAccessor>()
            .AddErrorFilter<ErrorFilter>()
            .AddMaxExecutionDepthRule(10)
            .DisableIntrospection(!isDevelopment)
            .ModifyOptions(o =>
            {
                // Each resolver gets its own DI scope, so parallel resolvers never share a DbContext.
                o.DefaultQueryDependencyInjectionScope = DependencyInjectionScope.Resolver;
                o.DefaultMutationDependencyInjectionScope = DependencyInjectionScope.Request;
            })
            .ModifyCostOptions(o =>
            {
                // GQ-7: enforced. 5000 lets a 100-item page resolve one DataLoader-backed reference per item.
                o.EnforceCostLimits = true;
                o.MaxFieldCost = 5_000;
                o.MaxTypeCost = 5_000;
            })
            .ModifyRequestOptions(o =>
            {
                o.ExecutionTimeout = TimeSpan.FromSeconds(30);
                o.IncludeExceptionDetails = isDevelopment;
            });

        return graphql;
    }
}
