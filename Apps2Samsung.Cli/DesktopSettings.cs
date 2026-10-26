using System.Text.Json;
using System.Text.Json.Nodes;

namespace Apps2Samsung.Cli;

/// <summary>
/// The desktop app's <c>settings.json</c> in the shared data directory. The CLI reads the choices a
/// run should make the same way the desktop does (network adapter, extra scan IP, certificate
/// preference, extra DUIDs) and keeps the package-id renames (#839) in it, so an app installed
/// under another id by one is recognised by the other. A missing file reads as all-empty; a file
/// that does not parse is never written over.
/// </summary>
public sealed class DesktopSettings
{
    private const string RenamesKey = "PackageIdRenamesJson";

    private readonly string _path;
    private JsonObject? _root;
    private bool _writable;

    public DesktopSettings(string dataDir)
    {
        _path = Path.Combine(dataDir, "settings.json");
    }

    /// <summary>The adapter IP picked in Settings, empty for "automatic".</summary>
    public string LocalIp => Get("LocalIp");

    /// <summary>The extra IP the desktop folds into its scan.</summary>
    public string UserCustomIp => Get("UserCustomIP");

    /// <summary>Extra DUIDs to put in the distributor certificate.</summary>
    public string ManualDuids => Get("ManualDuids");

    /// <summary>Automatic / Public / Partner, as Core's <c>CertificatePreference</c> reads it.</summary>
    public string CertificatePreference
    {
        get
        {
            var preference = Get("CertificatePreference");
            // A settings file from before the three-way choice only has the old on/off toggle.
            if (preference.Length == 0 && Root["PartnerSigning"] is JsonValue v && v.TryGetValue<bool>(out var partner) && partner)
                return Apps2Samsung.Certificate.CertificatePreference.Partner;
            return Apps2Samsung.Certificate.CertificatePreference.Normalize(preference);
        }
    }

    public string PackageIdRenamesJson
    {
        get => Get(RenamesKey);
        set
        {
            _ = Root;
            if (!_writable)
                throw new InvalidOperationException($"{_path} could not be read, so the package-id rename was not saved in it.");
            Root[RenamesKey] = value ?? string.Empty;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
    }

    private JsonObject Root => _root ??= Load();

    private JsonObject Load()
    {
        if (!File.Exists(_path))
        {
            _writable = true;
            return new JsonObject();
        }
        try
        {
            if (JsonNode.Parse(File.ReadAllText(_path)) is JsonObject root)
            {
                _writable = true;
                return root;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new JsonObject();
    }

    private string Get(string key) =>
        Root[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : string.Empty;
}
