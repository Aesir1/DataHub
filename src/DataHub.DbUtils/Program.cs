using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using DataHub.Auth;
using DataHub.DbUtils;
using DataHub.DbUtils.Contexts;
using DataHub.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var context = new Option<DbContextName>("--context") { Description = "App, Auth or All.", DefaultValueFactory = _ => DbContextName.All };
var check = new Option<bool>("--check") { Description = "Exit with code 1 when migrations are pending; apply nothing." };
var force = new Option<bool>("--force") { Description = "Allow reset outside Development." };
var fixtures = new Option<bool>("--fixtures") { Description = "Insert deterministic fixture data (fixed ids)." };
var masterData = new Option<bool>("--master-data") { Description = "Upsert reference data from embedded JSON." };
var keycloakUsers = new Option<bool>("--keycloak-users") { Description = "Create dev users through the Keycloak Admin API." };
var vacuum = new Option<bool>("--vacuum-analyze") { Description = "VACUUM (ANALYZE) every table of the context." };
var purge = new Option<bool>("--purge-soft-deleted") { Description = "Hard-delete soft-deleted rows past --older-than." };
var olderThan = new Option<string>("--older-than") { Description = "Retention for --purge-soft-deleted, e.g. 90d or 12h.", DefaultValueFactory = _ => "90d" };
var orphans = new Option<bool>("--orphan-objects") { Description = "Report object-storage objects no row references." };
var delete = new Option<bool>("--delete") { Description = "With --orphan-objects: remove them." };

var migrateCommand = new Command("migrate", "Apply pending EF Core migrations (safe on shared environments).") { context, check };
migrateCommand.SetAction((parse, ct) => Run(async (sp, log) =>
{
    var pending = 0;
    foreach (var name in Contexts(parse.GetValue(context)))
    {
        pending += parse.GetValue(check)
            ? await Step(log, $"{name}: check", async () => (await PendingAsync(sp, name, ct)).Count)
            : await Step(log, $"{name}: migrate", () => MigrateAsync(sp, name, ct));
    }

    return parse.GetValue(check) && pending > 0 ? 1 : 0;
}));

var resetCommand = new Command("reset", "Drop and recreate the database (Development only unless --force).") { context, force };
resetCommand.SetAction((parse, ct) => Run(async (sp, log) =>
{
    if (!RefuseOutsideDevelopment(sp, log, parse.GetValue(force)))
    {
        return 2;
    }

    foreach (var name in Contexts(parse.GetValue(context)))
    {
        await Step(log, $"{name}: reset", () => ResetAsync(sp, name, ct));
        await Step(log, $"{name}: migrate", () => MigrateAsync(sp, name, ct));
    }

    return 0;
}));

var seedCommand = new Command("seed", "Seed fixtures, master data and/or Keycloak dev users (idempotent).") { context, fixtures, masterData, keycloakUsers };
seedCommand.SetAction((parse, ct) => Run((sp, log) => SeedAsync(sp, log, parse.GetValue(context), parse.GetValue(fixtures), parse.GetValue(masterData), parse.GetValue(keycloakUsers), ct)));

var maintainCommand = new Command("maintain", "Maintenance: vacuum, purge soft-deleted rows, orphan objects.") { context, vacuum, purge, olderThan, orphans, delete };
maintainCommand.SetAction((parse, ct) => Run(async (sp, log) =>
{
    var options = new MaintainOptions(
        parse.GetValue(vacuum),
        parse.GetValue(purge) ? ParseAge(parse.GetValue(olderThan)!) : null,
        parse.GetValue(orphans),
        parse.GetValue(delete));
    foreach (var name in Contexts(parse.GetValue(context)))
    {
        await Step(log, $"{name}: maintain", () => name == DbContextName.App
            ? sp.GetRequiredService<AppContextCommands>().MaintainAsync(options, ct)
            : sp.GetRequiredService<AuthContextCommands>().MaintainAsync(options with { OrphanObjects = false, PurgeSoftDeletedOlderThan = null }, ct));
    }

    return 0;
}));

var reseedCommand = new Command("reseed", "reset + migrate + seed --fixtures --master-data --keycloak-users for every context (dbutils-seed).") { force };
reseedCommand.SetAction((parse, ct) => Run(async (sp, log) =>
{
    if (!RefuseOutsideDevelopment(sp, log, parse.GetValue(force)))
    {
        return 2;
    }

    foreach (var name in Contexts(DbContextName.All))
    {
        await Step(log, $"{name}: reset", () => ResetAsync(sp, name, ct));
        await Step(log, $"{name}: migrate", () => MigrateAsync(sp, name, ct));
    }

    return await SeedAsync(sp, log, DbContextName.All, fixtures: true, masterData: true, keycloakUsers: true, ct);
}));

var root = new RootCommand("DataHub database utility") { migrateCommand, resetCommand, seedCommand, maintainCommand, reseedCommand };
return await root.Parse(args).InvokeAsync();

static async Task<int> Run(Func<IServiceProvider, ILogger, Task<int>> action)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Configuration.AddJsonFile("appsettings.User.json", optional: true);
    builder.AddServiceDefaults();
    builder.AddDbUtils();

    using var host = builder.Build();
    var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DbUtils");
    await using var scope = host.Services.CreateAsyncScope();
    var total = Stopwatch.StartNew();
    try
    {
        var code = await action(scope.ServiceProvider, log);
        log.LogInformation("Finished with exit code {Code} in {Elapsed} ms", code, total.ElapsedMilliseconds);
        return code;
    }
    catch (Exception ex)
    {
        log.LogError(ex, "DbUtils failed after {Elapsed} ms", total.ElapsedMilliseconds);
        return 1;
    }
}

static IEnumerable<DbContextName> Contexts(DbContextName name) => name == DbContextName.All ? [DbContextName.App, DbContextName.Auth] : [name];

static Task<int> MigrateAsync(IServiceProvider sp, DbContextName name, CancellationToken ct) => name == DbContextName.App
    ? sp.GetRequiredService<AppContextCommands>().MigrateAsync(ct)
    : sp.GetRequiredService<AuthContextCommands>().MigrateAsync(ct);

static Task<IReadOnlyList<string>> PendingAsync(IServiceProvider sp, DbContextName name, CancellationToken ct) => name == DbContextName.App
    ? sp.GetRequiredService<AppContextCommands>().PendingMigrationsAsync(ct)
    : sp.GetRequiredService<AuthContextCommands>().PendingMigrationsAsync(ct);

static Task<int> ResetAsync(IServiceProvider sp, DbContextName name, CancellationToken ct) => name == DbContextName.App
    ? sp.GetRequiredService<AppContextCommands>().ResetAsync(ct)
    : sp.GetRequiredService<AuthContextCommands>().ResetAsync(ct);

static async Task<int> SeedAsync(IServiceProvider sp, ILogger log, DbContextName context, bool fixtures, bool masterData, bool keycloakUsers, CancellationToken ct)
{
    foreach (var name in Contexts(context))
    {
        if (masterData)
        {
            await Step(log, $"{name}: seed master data", () => name == DbContextName.App
                ? sp.GetRequiredService<AppContextCommands>().SeedMasterDataAsync(ct)
                : sp.GetRequiredService<AuthContextCommands>().SeedMasterDataAsync(ct));
        }

        if (fixtures)
        {
            await Step(log, $"{name}: seed fixtures", () => name == DbContextName.App
                ? sp.GetRequiredService<AppContextCommands>().SeedFixturesAsync(ct)
                : sp.GetRequiredService<AuthContextCommands>().SeedFixturesAsync(ct));
        }
    }

    if (keycloakUsers)
    {
        await Step(log, "keycloak: seed users", () => sp.GetRequiredService<KeycloakUsers>().SeedAsync(ct));
    }

    return 0;
}

static bool RefuseOutsideDevelopment(IServiceProvider sp, ILogger log, bool force)
{
    if (sp.GetRequiredService<IHostEnvironment>().IsDevelopment() || force)
    {
        return true;
    }

    log.LogError("Refusing to reset outside Development; pass --force to override");
    return false;
}

static TimeSpan ParseAge(string value) => value[^1] switch
{
    'd' => TimeSpan.FromDays(int.Parse(value[..^1], CultureInfo.InvariantCulture)),
    'h' => TimeSpan.FromHours(int.Parse(value[..^1], CultureInfo.InvariantCulture)),
    _ => TimeSpan.Parse(value, CultureInfo.InvariantCulture),
};

static async Task<int> Step(ILogger log, string name, Func<Task<int>> action)
{
    log.LogInformation("{Step} started", name);
    var sw = Stopwatch.StartNew();
    var rows = await action();
    log.LogInformation("{Step} done: {Rows} rows in {Elapsed} ms", name, rows, sw.ElapsedMilliseconds);
    return rows;
}
