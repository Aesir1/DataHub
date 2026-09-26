using System.Reflection;

namespace DataHub.Infrastructure.Persistence.Sql;

/// <summary>DB-4: raw SQL lives in embedded .sql files and is applied from code-first migrations.</summary>
public static class SqlScripts
{
    public static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"DataHub.Infrastructure.Sql.{name}")
            ?? throw new FileNotFoundException($"Embedded SQL script {name} not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
