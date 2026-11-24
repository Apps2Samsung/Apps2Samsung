using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Apps2Samsung.Sdb
{
    /// <summary>What became of an app the TV was asked to remove.</summary>
    public enum UninstallOutcome
    {
        /// <summary>The app is no longer in the TV's list.</summary>
        Removed,
        /// <summary>The TV says it was not there to begin with (<c>failed[132]</c>), and it is not listed.</summary>
        NotInstalled,
        /// <summary>The TV still lists the app: it refused, in words or without any.</summary>
        Kept,
    }

    /// <param name="Reply">The TV's own answer to the request, null when it gave none.</param>
    /// <param name="Verified">The outcome was read off the TV's app list, not off the answer.</param>
    public sealed record UninstallVerdict(UninstallOutcome Outcome, string? Reply, bool Verified);

    /// <summary>
    /// Removes an app and says what the TV did with the request (#1175). Shared by every head, so an
    /// app the TV lists as one of its own (<c>is_removable = 0</c>) gets the same question and the
    /// same outcome everywhere, and the same as TV-Store's On the TV page.
    /// </summary>
    /// <remarks>
    /// The app counts as removed when the list read again no longer has it. The words of the answer
    /// are not trusted on their own: some sets refuse a store or preinstalled app with no text at
    /// all (#855), and <c>failed[132]</c> is "was not there", not a failure.
    /// </remarks>
    public static class AppUninstall
    {
        /// <summary>The TV's code for "this app is not installed".</summary>
        public const string NotInstalledCode = "failed[132]";

        /// <summary>The question before removing an app the TV lists as one of its own ({0} = its name).</summary>
        public const string ConfirmBuiltIn =
            "The TV lists {0} as one of its own apps. It may refuse to remove it. If it does remove it, " +
            "Apps2Samsung cannot put it back: it would have to come from the TV's own Apps store again.";

        /// <summary>The TV kept one of its own apps ({0} = its name, {1} = the TV's answer).</summary>
        public const string KeptBuiltIn =
            "The TV did not remove {0}. It lists the app as one of its own, and those it keeps (it answered: {1}).";

        /// <summary>As <see cref="KeptBuiltIn"/>, for a TV that answered nothing.</summary>
        public const string KeptBuiltInSilent =
            "The TV did not remove {0}. It lists the app as one of its own, and those it keeps. It gave no answer.";

        /// <summary>
        /// Asks the TV to remove <paramref name="tizenId"/> and reads its app list again for the outcome.
        /// A list that cannot be read afterwards leaves the answer as the only evidence.
        /// </summary>
        public static async Task<UninstallVerdict> RunAsync(ISdbEngine sdb, string tvIpAddress, string tizenId)
        {
            var result = await sdb.UninstallAsync(tvIpAddress, tizenId).ConfigureAwait(false);

            IReadOnlyList<InstalledApp>? after = null;
            try
            {
                after = TizenInstalledApps.ParseOrThrow(await sdb.AppsAsync(tvIpAddress).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[uninstall] {tizenId}: the app list could not be read back, going by the TV's answer: {ex.Message}");
            }

            var verdict = Judge(result, after, tizenId);
            Trace.WriteLine($"[uninstall] {tizenId}: {verdict.Outcome} ({(verdict.Verified ? "app list" : "answer only")}); the TV answered: {verdict.Reply ?? "(nothing)"}");
            return verdict;
        }

        /// <summary>
        /// The outcome of an uninstall, from the TV's answer and the app list read after it
        /// (<paramref name="listedAfter"/>, null when it could not be read).
        /// </summary>
        public static UninstallVerdict Judge(ProcessResult result, IEnumerable<InstalledApp>? listedAfter, string tizenId)
        {
            var reply = ReplyOf(result);
            var notInstalled = reply?.Contains(NotInstalledCode, StringComparison.OrdinalIgnoreCase) ?? false;

            if (listedAfter is not null)
            {
                if (listedAfter.Any(app => string.Equals(app.TizenId, tizenId, StringComparison.OrdinalIgnoreCase)))
                    return new UninstallVerdict(UninstallOutcome.Kept, reply, Verified: true);
                return new UninstallVerdict(notInstalled ? UninstallOutcome.NotInstalled : UninstallOutcome.Removed, reply, Verified: true);
            }

            if (notInstalled)
                return new UninstallVerdict(UninstallOutcome.NotInstalled, reply, Verified: false);
            return new UninstallVerdict(result.ExitCode == 0 ? UninstallOutcome.Removed : UninstallOutcome.Kept, reply, Verified: false);
        }

        /// <summary>
        /// What to tell the user when the TV kept an app it lists as one of its own, from the two
        /// templates a head supplies (its translations of <see cref="KeptBuiltIn"/> and
        /// <see cref="KeptBuiltInSilent"/>).
        /// </summary>
        public static string KeptBuiltInMessage(string appName, string? reply, string withReply = KeptBuiltIn, string silent = KeptBuiltInSilent) =>
            string.IsNullOrWhiteSpace(reply) ? string.Format(silent, appName) : string.Format(withReply, appName, reply);

        // The answer in the TV's words: the error when the engine reported one, else what it printed.
        private static string? ReplyOf(ProcessResult result)
        {
            var text = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
    }
}
