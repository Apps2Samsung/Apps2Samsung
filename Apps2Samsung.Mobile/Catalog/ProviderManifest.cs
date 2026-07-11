namespace Apps2Samsung.Mobile.Catalog;

/// <summary>
/// The app catalog manifest (third-party-apps.json). Trimmed to the fields the mobile head needs
/// to build the Release/Version lists; extra fields in the JSON (preview images, community apps,
/// build info) are ignored on deserialization.
/// </summary>
public sealed class ProviderManifest
{
	public int SchemaVersion { get; set; } = 1;
	public List<ProviderEntry> Providers { get; set; } = new();
}

public sealed class ProviderEntry
{
	public string Id { get; set; } = string.Empty;
	public string Url { get; set; } = string.Empty;
	public string Prefix { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	public int Take { get; set; } = 1;

	/// <summary>
	/// When true, every .wgt asset of the fetched release becomes its own entry in the release
	/// list (used for the Tizen Community package bundle).
	/// </summary>
	public bool ExpandAssets { get; set; }
}
