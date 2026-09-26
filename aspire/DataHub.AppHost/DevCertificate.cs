using System.Diagnostics;

namespace DataHub.AppHost;

/// <summary>
/// Exports the ASP.NET Core HTTPS development certificate (the one <c>dotnet dev-certs https --trust</c> trusts)
/// for processes that cannot read it from the .NET store: Azure Functions Core Tools needs a PFX, Next.js a
/// PEM certificate and key, and Node a CA file for calling the Api over HTTPS.
/// </summary>
internal sealed record DevCertificate(string PfxPath, string PemPath, string KeyPath, string Password)
{
    public static DevCertificate Export(string directory, string password)
    {
        Directory.CreateDirectory(directory);
        var certificate = new DevCertificate(
            Path.Combine(directory, "localhost.pfx"),
            Path.Combine(directory, "localhost.pem"),
            Path.Combine(directory, "localhost.key"),
            password);

        // Re-exported on every start so a renewed dev certificate is picked up.
        Run($"dev-certs https --export-path \"{certificate.PfxPath}\" --password \"{password}\"");
        Run($"dev-certs https --export-path \"{certificate.PemPath}\" --format Pem --no-password");
        return certificate;
    }

    private static void Run(string arguments)
    {
        // The SDK on PATH is the one that owns the dev certificate store.
#pragma warning disable S4036
        using var process = Process.Start(new ProcessStartInfo("dotnet", arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
#pragma warning restore S4036
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet {arguments} failed ({process.ExitCode}): {error}. Run `dotnet dev-certs https --trust` first.");
        }
    }
}
