using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apps2Samsung.Configuration;
using Apps2Samsung.Helpers.API;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Helpers.Jellyfin.Plugins;

// Usage: PluginPatchRig <work dir> <output dir>
//
// For every case in cases.json: clones the plugin's repository at the case's release tag, serves its
// client files from a fake Jellyfin server (an in-memory HttpMessageHandler, so jsDelivr and
// raw.githubusercontent.com fallbacks are answered from the same clones), runs the real
// JellyfinPluginPatcher and checks the result. plugin_cache of each case is copied to
// <output dir>/<case id>/ for the ES2015 parse step that follows in CI.

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: PluginPatchRig <work dir> <output dir>");
    return 2;
}

var workDir = Path.GetFullPath(args[0]);
var outputDir = Path.GetFullPath(args[1]);
Directory.CreateDirectory(workDir);
if (Directory.Exists(outputDir))
    Directory.Delete(outputDir, recursive: true);
Directory.CreateDirectory(outputDir);

var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
var cases = JsonSerializer.Deserialize<CaseFile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cases.json")), jsonOptions)!.Cases;
var repos = new RepoCache(workDir);

int failedCases = 0;
foreach (var rigCase in cases)
{
    var problems = await RunCaseAsync(rigCase);
    Console.WriteLine(problems.Count == 0 ? $"✓ {rigCase.Id}" : $"✗ {rigCase.Id}");
    foreach (var problem in problems)
        Console.WriteLine($"    {problem}");
    if (problems.Count > 0)
        failedCases++;
}

Console.WriteLine(failedCases == 0 ? $"\nAll {cases.Count} cases pass." : $"\n{failedCases} of {cases.Count} cases failed.");
return failedCases == 0 ? 0 : 1;

async Task<List<string>> RunCaseAsync(RigCase rigCase)
{
    var problems = new List<string>();
    var caseOut = Path.Combine(outputDir, rigCase.Id);
    Directory.CreateDirectory(caseOut);

    var log = new StringWriter();
    var listener = new TextWriterTraceListener(log);
    Trace.Listeners.Add(listener);

    var server = new FakeJellyfin(rigCase, repos.Checkout(rigCase.Repo, rigCase.Ref), repos);
    var packagePath = CreateEmptyPackage(Path.Combine(workDir, "packages", rigCase.Id));
    try
    {
        using var workspace = PackageWorkspace.Extract(packagePath, targetDeviceIp: null, Version.Parse(rigCase.Tizen ?? "9.0"));
        var http = new HttpClient(server);
        var api = new JellyfinApiClient(http);
        var patcher = new JellyfinPluginPatcher(http, api, new PluginManager(http, api), RigConfig.Create());

        var css = new StringBuilder();
        var head = new StringBuilder();
        var body = new StringBuilder();
        await patcher.PatchPluginsAsync(workspace, FakeJellyfin.ServerUrl, css, head, body);

        var pluginCache = Path.Combine(workspace.Root, "www", "plugin_cache");
        var tags = $"{css}\n{head}\n{body}";
        File.WriteAllText(Path.Combine(caseOut, "tags.html"), tags);
        if (Directory.Exists(pluginCache))
            CopyDirectory(pluginCache, Path.Combine(caseOut, "plugin_cache"));

        Check(rigCase.Expect ?? new Expectations(), pluginCache, tags, server.Requests, log.ToString(), problems);
    }
    catch (Exception ex)
    {
        problems.Add($"crashed: {ex}");
    }
    finally
    {
        Trace.Listeners.Remove(listener);
        File.WriteAllText(Path.Combine(caseOut, "log.txt"), log + "\nRequests:\n" + string.Join("\n", server.Requests));
    }

    return problems;
}

static void Check(Expectations expect, string pluginCache, string tags, List<string> requests, string log, List<string> problems)
{
    foreach (var file in expect.Files)
    {
        if (!File.Exists(Path.Combine(pluginCache, file)))
            problems.Add($"expected plugin_cache/{file}, not there");
    }

    foreach (var (dir, min) in expect.MinScripts)
    {
        var full = Path.Combine(pluginCache, dir);
        int count = Directory.Exists(full) ? Directory.GetFiles(full, "*.js", SearchOption.AllDirectories).Length : 0;
        if (count < min)
            problems.Add($"expected at least {min} scripts in plugin_cache/{dir}, found {count}");
    }

    foreach (var dir in expect.Skipped)
    {
        if (Directory.Exists(Path.Combine(pluginCache, dir)) || tags.Contains($"plugin_cache/{dir}/", StringComparison.Ordinal))
            problems.Add($"expected {dir} to be skipped, but it was cached or injected");
    }

    foreach (var url in expect.Requested)
    {
        if (!requests.Any(r => r.Contains(url, StringComparison.Ordinal)))
            problems.Add($"expected a request for {url}");
    }

    foreach (var url in expect.NotRequested)
    {
        foreach (var r in requests.Where(r => r.Contains(url, StringComparison.Ordinal)))
            problems.Add($"unexpected request: {r}");
    }

    // Every injected plugin_cache script or stylesheet must exist, or the app loads a 404.
    foreach (Match m in Regex.Matches(tags, @"(?:src|href)=""plugin_cache/([^""]+)"""))
    {
        if (!File.Exists(Path.Combine(pluginCache, m.Groups[1].Value)))
            problems.Add($"injected plugin_cache/{m.Groups[1].Value}, but it isn't in the package");
    }

    // A failed transpile leaves a file out (EsbuildHelper); in the rig that's always a failure.
    foreach (var line in log.Split('\n').Where(l => l.Contains('⛔')))
        problems.Add(line.Trim());
}

static string CreateEmptyPackage(string dir)
{
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "Jellyfin.wgt");
    if (File.Exists(path))
        File.Delete(path);
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    using var writer = new StreamWriter(zip.CreateEntry("www/index.html").Open());
    writer.Write("<!DOCTYPE html><html><head></head><body></body></html>");
    return path;
}

static void CopyDirectory(string from, string to)
{
    foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(to, Path.GetRelativePath(from, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}

/// <summary>
/// Answers the patcher's requests the way a Jellyfin server with the case's plugin installed would,
/// from a checkout of the plugin's repository. Requests to jsDelivr and raw.githubusercontent.com
/// (the patches' GitHub fallbacks) are answered from a checkout of the repository at that ref.
/// Everything else is a 404. Every request is recorded with its status.
/// </summary>
sealed class FakeJellyfin(RigCase rigCase, string? checkout, RepoCache repos) : HttpMessageHandler
{
    public const string ServerUrl = "http://jellyfin.test";

    private static readonly Regex JsDelivr = new(@"^https://cdn\.jsdelivr\.net/gh/([^/]+/[^/@]+)@([^/]+)/(.+)$");
    private static readonly Regex RawGitHub = new(@"^https://raw\.githubusercontent\.com/([^/]+/[^/]+)/([^/]+)/(.+)$");

    public List<string> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        var content = Resolve(url);
        Requests.Add($"{(content == null ? 404 : 200)} {url}");

        var response = new HttpResponseMessage(content == null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(content ?? Array.Empty<byte>()),
        };
        return Task.FromResult(response);
    }

    private byte[]? Resolve(string url)
    {
        var external = JsDelivr.Match(url) is { Success: true } j ? j : RawGitHub.Match(url);
        if (external.Success)
            return ReadFile(repos.Checkout(external.Groups[1].Value, external.Groups[2].Value), external.Groups[3].Value);

        if (!url.StartsWith(ServerUrl, StringComparison.Ordinal))
            return null;

        var path = url.Substring(ServerUrl.Length);
        if (path.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase))
        {
            var scripts = string.Concat(rigCase.Index.Select(src => $"<script src=\"{src}\"></script>"));
            return Encoding.UTF8.GetBytes($"<!DOCTYPE html><html><head></head><body>{scripts}</body></html>");
        }

        if (path.Equals("/Plugins", StringComparison.OrdinalIgnoreCase))
        {
            var plugins = rigCase.Plugin == null
                ? Array.Empty<object>()
                : new object[] { new { Name = rigCase.Plugin.Name, Id = "00000000000000000000000000000001", Version = rigCase.Plugin.Version } };
            return JsonSerializer.SerializeToUtf8Bytes(plugins);
        }

        // Jellyfin's routing is case-insensitive; prefix entries (ending in "/") map a whole folder.
        foreach (var (route, source) in rigCase.Server)
        {
            if (route.EndsWith('/'))
            {
                if (path.StartsWith(route, StringComparison.OrdinalIgnoreCase))
                    return ReadFile(checkout, source + path.Substring(route.Length));
            }
            else if (path.Equals(route, StringComparison.OrdinalIgnoreCase))
            {
                return Serve(source);
            }
        }

        return null;
    }

    // "=text" is literal content, "@je-bundle" builds Jellyfin Enhanced's /bundle.js, anything else
    // is a path in the checkout.
    private byte[]? Serve(string source)
    {
        if (source.StartsWith('='))
            return Encoding.UTF8.GetBytes(source.Substring(1));
        if (source == "@je-bundle")
            return JellyfinEnhancedBundle();
        return ReadFile(checkout, source);
    }

    // Same shape as JE's ClientScriptBundle (12.10+): every file of js/component-scripts.json, in
    // order, wrapped in a function in one array.
    private byte[]? JellyfinEnhancedBundle()
    {
        const string js = "Jellyfin.Plugin.JellyfinEnhanced/js/";
        var manifest = ReadFile(checkout, js + "component-scripts.json");
        if (manifest == null)
            return null;

        var bundle = new StringBuilder("window.__JE_BUNDLE_MODULES = [\n");
        foreach (var entry in JsonSerializer.Deserialize<string[]>(manifest)!.Where(e => !e.StartsWith("//", StringComparison.Ordinal)))
        {
            var file = ReadFile(checkout, js + entry);
            if (file == null)
                return null;
            bundle.Append($"/* ---- js/{entry} ---- */\nfunction () {{\n{Encoding.UTF8.GetString(file)}\n}},\n");
        }
        bundle.Append("];\n");
        return Encoding.UTF8.GetBytes(bundle.ToString());
    }

    private static byte[]? ReadFile(string? root, string relPath)
    {
        if (root == null)
            return null;
        var full = Path.GetFullPath(Path.Combine(root, relPath));
        return full.StartsWith(root, StringComparison.Ordinal) && File.Exists(full) ? File.ReadAllBytes(full) : null;
    }
}

/// <summary>Shallow clones of plugin repositories, one per repository and ref, made on first use.</summary>
sealed class RepoCache(string workDir)
{
    private readonly Dictionary<string, string?> _checkouts = new(StringComparer.OrdinalIgnoreCase);

    public string? Checkout(string repo, string gitRef)
    {
        var key = $"{repo}@{gitRef}";
        if (_checkouts.TryGetValue(key, out var known))
            return known;

        var dir = Path.Combine(workDir, "repos", repo.Replace('/', '_') + "@" + gitRef);
        if (!Directory.Exists(dir))
        {
            var git = Process.Start(new ProcessStartInfo("git", ["clone", "--quiet", "--depth", "1", "--branch", gitRef, $"https://github.com/{repo}", dir])
            {
                RedirectStandardError = true,
            })!;
            var error = git.StandardError.ReadToEnd();
            git.WaitForExit();
            if (git.ExitCode != 0)
            {
                Console.WriteLine($"  (could not clone {key}: {error.Trim()})");
                dir = null;
            }
        }

        _checkouts[key] = dir is null ? null : Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        return _checkouts[key];
    }
}

/// <summary>An IAppConfig with every setting at its empty default: no plugin opted out.</summary>
class RigConfig : System.Reflection.DispatchProxy
{
    public static IAppConfig Create() => Create<IAppConfig, RigConfig>();

    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        => method!.ReturnType == typeof(string) ? string.Empty
            : method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType)
            : null;
}

sealed record CaseFile(List<RigCase> Cases);

sealed record RigCase(
    string Id,
    string Repo,
    string Ref,
    PluginInfo? Plugin,
    string? Tizen,
    List<string> Index,
    Dictionary<string, string> Server,
    Expectations? Expect)
{
    public List<string> Index { get; init; } = Index ?? new();
    public Dictionary<string, string> Server { get; init; } = Server ?? new();
}

sealed record PluginInfo(string Name, string Version);

sealed record Expectations
{
    public List<string> Files { get; init; } = new();
    public Dictionary<string, int> MinScripts { get; init; } = new();
    public List<string> Skipped { get; init; } = new();
    public List<string> Requested { get; init; } = new();
    public List<string> NotRequested { get; init; } = new();
}
