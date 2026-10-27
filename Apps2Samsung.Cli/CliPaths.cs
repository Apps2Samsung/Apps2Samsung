using System.Reflection;
using Apps2Samsung.Portability;

namespace Apps2Samsung.Cli;

/// <summary>
/// Where the CLI keeps its files. The data directory is the desktop app's
/// (<c>%APPDATA%\Apps2Samsung</c>, <c>~/.config/Apps2Samsung</c>), so a certificate the desktop
/// already made for a TV is reused here without a second Samsung sign-in, and the other way round.
/// <c>APPS2SAMSUNG_DATA_DIR</c> or <c>--data-dir</c> points it elsewhere.
/// </summary>
public static class CliPaths
{
    public static string DataDir { get; private set; } = DefaultDataDir();

    public static void Use(string? dataDir)
    {
        if (!string.IsNullOrWhiteSpace(dataDir))
            DataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(DataDir);
    }

    private static string DefaultDataDir()
    {
        var env = Environment.GetEnvironmentVariable("APPS2SAMSUNG_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "Apps2Samsung");
    }

    /// <summary>The generated signing profiles ("Jelly2Sams - Public/Partner"), shared with the desktop.</summary>
    public static string CertificateStore => Path.Combine(DataDir, "Certificate");

    /// <summary>The CLI's own files: SDB key, CA copies, catalog caches.</summary>
    public static string CliDir => Path.Combine(DataDir, "cli");

    public static string CaDir => Path.Combine(CliDir, "ca");

    /// <summary>Downloaded packages: throwaway, so the OS cache location.</summary>
    public static string Downloads => UserPaths.Cache(CliDir, "downloads");

    private static readonly string[] CaFiles = { "vd_tizen_dev_author_ca.cer", "vd_tizen_dev_public2.crt", "vd_tizen_dev_partner2.crt" };

    /// <summary>Writes the embedded Samsung CA files out once and returns their directory.</summary>
    public static string EnsureCaFiles()
    {
        Directory.CreateDirectory(CaDir);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var file in CaFiles)
        {
            var dest = Path.Combine(CaDir, file);
            if (File.Exists(dest))
                continue;
            using var src = assembly.GetManifestResourceStream($"ca.{file}")
                ?? throw new InvalidOperationException($"Embedded CA file '{file}' is missing from this build.");
            using var dst = File.Create(dest);
            src.CopyTo(dst);
        }
        return CaDir;
    }
}
