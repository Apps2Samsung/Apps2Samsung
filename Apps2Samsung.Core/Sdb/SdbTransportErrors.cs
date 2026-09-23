using Apps2Samsung.Models;
using System;
using System.Linq;

namespace Apps2Samsung.Sdb
{
    /// <summary>
    /// Tells a dead SDB link apart from an answer the TV actually gave.
    ///
    /// Samsung's sdbd does not reply "no" to a verb it dislikes: it closes the whole connection, and
    /// the command that was in flight comes back as a socket error rather than as a refusal (the
    /// consumer-set <c>was_execute</c> behaviour behind PR #659). Anything reading such a result has
    /// learned nothing about the TV's opinion — only that the link died — so it either retries on a
    /// fresh connection or says so, instead of reporting the socket error as the TV's verdict.
    /// </summary>
    public static class SdbTransportErrors
    {
        // The wording comes from the socket layer, not from the TV, so these are the .NET/TizenSdb
        // phrasings rather than anything sdbd sends.
        private static readonly string[] Markers =
        {
            "forcibly closed by the remote host",
            "Remote closed stream while reading",
            "Unable to read data from the transport connection",
            "Connection reset by peer",
        };

        /// <summary>Whether <paramref name="result"/> failed because the link died rather than because the TV said no.</summary>
        public static bool IsTransient(ProcessResult? result) =>
            result is not null && result.ExitCode != 0 && IsTransient($"{result.Error} {result.Output}");

        /// <summary>Whether <paramref name="text"/> carries a transport failure's wording.</summary>
        public static bool IsTransient(string? text) =>
            !string.IsNullOrWhiteSpace(text) &&
            Markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
