using DataHub.Api;
using DataHub.Application;
using DataHub.Auth;
using DataHub.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.User.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

builder.AddServiceDefaults();
builder.Services.AddApplication(builder.Configuration);
builder.AddInfrastructure();
builder.AddMessaging();
builder.AddDataHubAuth();
builder.Services.AddApi(builder.Configuration, builder.Environment.IsDevelopment());

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints();
app.MapGraphQL().RequireAuthorization();
app.MapFileEndpoints();

await app.RunWithGraphQLCommandsAsync(args);

#pragma warning disable S1118 // WebApplicationFactory<Program> needs a public, non-static Program.
public partial class Program;
#pragma warning restore S1118
