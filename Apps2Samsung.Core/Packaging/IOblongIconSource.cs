using System;
using System.IO;

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Supplies a head's bundled 16:9 "oblong" launcher tile for a package. The desktop implements it
    /// over Avalonia <c>avares://</c> assets; a head with no bundled tiles uses
    /// <see cref="NoOblongIconSource"/>. Lets the shared <see cref="CustomIconPackagePatcher"/> apply
    /// the "oblong" icon choice without any UI-framework dependency in Core.
    /// </summary>
    public interface IOblongIconSource
    {
        /// <summary>A stream opener for the oblong tile matching <paramref name="packageFileName"/> plus
        /// the fallback icon file to write when config.xml has no <c>&lt;icon src&gt;</c> — or null when
        /// this package has no bundled oblong tile on this head.</summary>
        (Func<Stream> OpenStream, string FallbackIconFile)? TryGetOblong(string packageFileName);
    }

    /// <summary>An oblong source that never has a tile — for heads without bundled oblong assets
    /// (e.g. mobile), where only user-supplied custom PNG icons apply.</summary>
    public sealed class NoOblongIconSource : IOblongIconSource
    {
        public (Func<Stream> OpenStream, string FallbackIconFile)? TryGetOblong(string packageFileName) => null;
    }
}
