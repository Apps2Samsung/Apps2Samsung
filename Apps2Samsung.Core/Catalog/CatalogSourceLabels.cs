using System;
using Apps2Samsung.Catalog.Repositories;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>How an app's source line reads (#1018): nothing for the official catalog.</summary>
    public enum SourceLineKind
    {
        None,
        /// <summary>A signed repository: its name, with the shield.</summary>
        Repository,
        /// <summary>An unsigned source the user added: "Unverified · name".</summary>
        Unverified,
        /// <summary>A repository listed from its last verified copy: "name · can't verify now", not installable.</summary>
        Blocked,
    }

    /// <summary>One source line under an app, as every head draws it (#1018, mockup row 2 A).</summary>
    public sealed record SourceLine(SourceLineKind Kind, string Text, string Tip)
    {
        public bool IsVisible => Kind != SourceLineKind.None;
    }

    /// <summary>
    /// The words for where an app comes from and how a source of the user's is doing (#1018), so the
    /// desktop and phone lists read the same. All text comes from en.json keys through
    /// <paramref name="localize"/>.
    /// </summary>
    public static class CatalogSourceLabels
    {
        public static SourceLine Line(CatalogSource source, Func<string, string> localize) => source switch
        {
            { Kind: CatalogSourceKind.Official } => new SourceLine(SourceLineKind.None, string.Empty, string.Empty),
            { Kind: CatalogSourceKind.Repository, CanInstall: false } =>
                new SourceLine(SourceLineKind.Blocked, string.Format(localize("lblSourceBlockedLine"), source.Name), localize("lblSourceBlockedTip")),
            { Kind: CatalogSourceKind.Repository } =>
                new SourceLine(SourceLineKind.Repository, source.Name, localize("lblSourceRepositoryTip")),
            _ => new SourceLine(SourceLineKind.Unverified, string.Format(localize("lblSourceUnverifiedLine"), source.Name), localize("lblSourceUnverifiedTip")),
        };

        /// <summary>"Official catalog", a repository's name, or "Unverified · name": for a list of the sources offering one app.</summary>
        public static string Name(CatalogSource source, Func<string, string> localize) => source.Kind switch
        {
            CatalogSourceKind.Official => localize("lblSourceOfficial"),
            CatalogSourceKind.Repository => source.Name,
            _ => string.Format(localize("lblSourceUnverifiedLine"), source.Name),
        };

        /// <summary>A source's status in Settings > Your sources: the repository's last load, or "not signed".</summary>
        public static (string Text, RepositoryState? State) Status(UserSource source, Func<string, string> localize)
        {
            if (!UserSources.IsApprovedRepository(source))
                return (localize("lblUnverifiedNotSigned"), null);
            var load = SignedRepositories.LastLoad(source.Url);
            return load?.State switch
            {
                RepositoryState.Ok => (localize("lblRepoStatusOk"), RepositoryState.Ok),
                RepositoryState.Stale => (string.Format(localize("lblRepoStatusStale"), load.Manifest!.GeneratedAt.ToLocalTime().ToString("d MMM")), RepositoryState.Stale),
                RepositoryState.KeyChanged => (localize("lblRepoStatusKeyChanged"), RepositoryState.KeyChanged),
                RepositoryState.Failed => (localize("lblRepoStatusFailed"), RepositoryState.Failed),
                _ => (localize("lblRepoStatusSigned"), null),
            };
        }
    }
}
