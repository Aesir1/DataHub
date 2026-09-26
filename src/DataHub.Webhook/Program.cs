using DataHub.Webhook;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
builder.Configuration.AddJsonFile("appsettings.User.json", optional: true).AddEnvironmentVariables();
builder.AddServiceDefaults();
builder.AddRabbitMQClient("rabbitmq");
builder.AddWebhook();
await builder.Build().RunAsync();
