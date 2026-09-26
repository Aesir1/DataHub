using DataHub.Auth.Persistence;
using DataHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DataHub.DbUtils;

/// <summary>Used by <c>dotnet ef migrations add</c> only; it never connects.</summary>
internal sealed class AppDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=app").Options);
}

internal sealed class AuthDesignTimeFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=localhost;Database=auth", o => o.MigrationsHistoryTable("__EFMigrationsHistory", AuthDbContext.Schema))
            .Options);
}
