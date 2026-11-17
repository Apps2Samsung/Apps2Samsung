using System.Reflection;
using System.Runtime.InteropServices;
using Apps2Samsung.Update;

namespace Apps2Samsung.Cli.Commands;

/// <summary>
/// <c>self-update</c> (#975): the CLI replaces itself with the newest release. The check is Core's
/// <see cref="GitHubUpdateChecker"/>, the one the desktop and phone apps use, with the CLI's own
/// archive for this platform; the download is Core's <see cref="UpdateDownloader"/>. Unlike the
/// desktop there is no window to restart, so the new files go straight over the running ones
/// (<see cref="UpdateFiles.Replace"/>) and the next start runs them.
/// <para>
/// Which releases count is the desktop's "Include beta updates" setting in the shared
/// settings.json, set here with <c>--channel stable|beta</c>. Where nothing chose yet (Termux has
/// no desktop app), a beta build follows the betas and a stable build the stable releases.
/// </para>
/// <para>
/// A beta is rebuilt under the tag it already has (<c>v3.3.1-beta</c> stays <c>v3.3.1-beta</c>),
/// so on that channel the version cannot say whether the release moved on. The build's commit can:
/// it is in this copy's version (<c>3.3.1-beta+&lt;commit&gt;</c>) and the tag points at the release's.
/// </para>
/// </summary>
public static class SelfUpdateCommand
{
    /// <summary>What a check found. <see cref="Error"/> set means the rest says nothing.</summary>
    public sealed record State(
        string Current, string Latest, bool Available, bool Rebuilt, bool Beta,
        string Rid, string? DownloadUrl, string ReleaseUrl, string? Error);

    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        if (s.Options.Arguments.Count > 0)
            return s.Out.Fail($"self-update takes no arguments\n{Help.For("self-update")}", Output.Usage);

        if (s.Options.Channel is { } channel)
        {
            try
            {
                s.Desktop.IncludeBetaUpdates = channel == "beta";
            }
            catch (InvalidOperationException ex)
            {
                // settings.json does not parse: the choice holds for this run only.
                s.Out.Warn(ex.Message);
            }
        }

        UpdateFiles.RemoveLeftovers(AppContext.BaseDirectory);
        s.Out.Progress("Checking for a newer version…");
        var state = await CheckAsync(s, ct);
        if (state.Error is not null)
            return s.Out.Fail(state.Error);

        if (!state.Available || s.Options.Check)
            return s.Out.Result(Json(state, updated: false), Describe(state));

        if (state.DownloadUrl is null)
            return s.Out.Fail($"{state.Latest} has no CLI build for {state.Rid} yet. A release takes a while to build; try again later, or see {state.ReleaseUrl}");

        var dir = AppContext.BaseDirectory;
        if (!CanWrite(dir))
            return s.Out.Fail($"{dir} cannot be written by this user, so the CLI cannot replace itself there. Update it the way it was installed, or download {state.DownloadUrl}");

        var archive = Path.Combine(CliPaths.Downloads, "self-update", Path.GetFileName(new Uri(state.DownloadUrl).LocalPath));
        // Inside the install folder: the same volume, so every file moves in with one rename.
        var staging = Path.Combine(dir, ".self-update");
        try
        {
            await new UpdateDownloader().DownloadAsync(state.DownloadUrl, archive,
                new Report(p => s.Out.Progress($"Downloading {state.Latest} {p.Percent}%")), ct);

            s.Out.Progress("Unpacking…");
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            await UpdateFiles.ExtractAsync(archive, staging, ct);

            // The Termux tarball unpacks to apps2samsung-cli/ (the assemblies and the launcher
            // script); every other platform ships the one executable at the top.
            var portable = IsPortable(Environment.ProcessPath);
            var root = portable ? Path.Combine(staging, "apps2samsung-cli") : staging;
            var main = portable ? "apps2samsung-cli.dll" : OperatingSystem.IsWindows() ? "apps2samsung-cli.exe" : "apps2samsung-cli";
            if (!File.Exists(Path.Combine(root, main)))
                return s.Out.Fail($"The download of {state.Latest} does not hold {main}, so nothing was changed. Download it by hand: {state.ReleaseUrl}");

            s.Out.Progress("Replacing this copy…");
            UpdateFiles.Replace(root, dir);
        }
        finally
        {
            TryDelete(() => Directory.Delete(staging, recursive: true));
            TryDelete(() => File.Delete(archive));
        }

        return s.Out.Result(Json(state, updated: true), new[]
        {
            $"Updated to {state.Latest}. The next start runs it.",
            $"In {dir}",
        });
    }

    /// <summary>The check alone, printing nothing: for <see cref="RunAsync"/> and for the menu.</summary>
    public static async Task<State> CheckAsync(CliServices s, CancellationToken ct)
    {
        var (version, commit) = Running();
        var current = "v" + version;
        var rid = Rid(Environment.ProcessPath, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture);
        var checker = new GitHubUpdateChecker(s.Http);
        var beta = FollowsBeta(s.Options.Channel, new DesktopSettings(CliPaths.DataDir).IncludeBetaUpdates, version);

        if (version.StartsWith("0.0.0", StringComparison.Ordinal))
            return new State(current, current, false, false, beta, rid, null, checker.ReleasesPageUrl,
                $"This is a local build ({version}); self-update is for the builds from {checker.ReleasesPageUrl}");

        var result = await checker.CheckForUpdateAsync(current, beta,
            assetMatcher: name => UpdateAssets.IsCliArchive(name, rid), cancellationToken: ct);
        if (!result.IsSuccess)
            return new State(current, current, false, false, beta, rid, null, checker.ReleasesPageUrl, result.ErrorMessage);

        var rebuilt = false;
        if (!result.IsUpdateAvailable && beta && commit is not null && version.Contains('-')
            && string.Equals(result.LatestVersion, current, StringComparison.OrdinalIgnoreCase))
        {
            var tagCommit = await checker.TagCommitAsync(result.LatestVersion, ct);
            if (tagCommit is not null && !tagCommit.StartsWith(commit, StringComparison.OrdinalIgnoreCase))
            {
                rebuilt = true;
                await checker.ResolveDownloadAsync(result, name => UpdateAssets.IsCliArchive(name, rid), cancellationToken: ct);
            }
        }

        var releaseUrl = string.IsNullOrWhiteSpace(result.ReleasesPageUrl) ? checker.ReleasesPageUrl : result.ReleasesPageUrl;
        return new State(current, result.LatestVersion, result.IsUpdateAvailable || rebuilt, rebuilt, beta, rid, result.DownloadUrl, releaseUrl, null);
    }

    /// <summary>The check in words, as the command prints it.</summary>
    public static IReadOnlyList<string> Describe(State state)
    {
        var follows = state.Beta ? "stable and beta releases" : "stable releases";
        var other = state.Beta
            ? "Stable releases only: apps2samsung-cli self-update --channel stable"
            : "Beta builds too: apps2samsung-cli self-update --channel beta";
        if (state.Available)
        {
            return new[]
            {
                state.Rebuilt
                    ? $"{state.Latest} was built again since this copy was made."
                    : $"{state.Latest} is available; this is {state.Current}.",
                "Install it with: apps2samsung-cli self-update",
            };
        }
        if (!string.Equals(state.Latest, state.Current, StringComparison.OrdinalIgnoreCase)
            && GitHubUpdateChecker.IsVersionGreater(state.Current, state.Latest))
        {
            return new[] { $"{state.Current} is newer than the newest of the {follows} ({state.Latest}).", other };
        }
        return new[] { $"{state.Current} is up to date (following {follows}).", other };
    }

    private static object Json(State state, bool updated) => new
    {
        ok = true,
        current = state.Current,
        latest = state.Latest,
        updateAvailable = state.Available,
        rebuilt = state.Rebuilt,
        channel = state.Beta ? "beta" : "stable",
        platform = state.Rid,
        updated,
        release = state.ReleaseUrl,
    };

    /// <summary>--channel for this run, else the shared setting, else what this build is.</summary>
    internal static bool FollowsBeta(string? channel, bool? setting, string version) =>
        channel is not null ? channel == "beta" : setting ?? version.Contains('-');

    /// <summary>
    /// The platform part of this build's archive name. The Termux build is the one that is not its
    /// own executable: it is assemblies run by Termux's <c>dotnet</c>.
    /// </summary>
    internal static string Rid(string? processPath, bool windows, bool macOs, Architecture architecture)
    {
        if (IsPortable(processPath))
            return "linux-bionic-arm64";
        var arch = architecture == Architecture.Arm64 ? "arm64" : "x64";
        return windows ? $"win-{arch}" : macOs ? $"osx-{arch}" : $"linux-{arch}";
    }

    internal static bool IsPortable(string? processPath) =>
        string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>"3.3.1-beta+0123abc…" as (version, commit); the commit is null when the build has none.</summary>
    internal static (string Version, string? Commit) Running() => Split(
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    internal static (string Version, string? Commit) Split(string? informationalVersion)
    {
        var parts = (informationalVersion ?? "0.0.0").Split('+', 2);
        var commit = parts.Length == 2 && parts[1].Length >= 7 && parts[1].All(Uri.IsHexDigit) ? parts[1] : null;
        return (parts[0], commit);
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".self-update-{Environment.ProcessId}");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(Action delete)
    {
        try { delete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Progress<T> posts to the thread pool, where 40% can land after 60%; this reports in order.
    private sealed class Report(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        void IProgress<DownloadProgress>.Report(DownloadProgress value) => report(value);
    }
}
