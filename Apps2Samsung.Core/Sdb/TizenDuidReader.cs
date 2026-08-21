using System;
using System.Linq;
using System.Threading.Tasks;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;

namespace Apps2Samsung.Sdb
{
    /// <summary>
    /// Reads a TV's DUID over SDB with a few retries. A single read is a coin flip on old/slow TVs
    /// (e.g. a 2016 Tizen 2.4 set): Samsung's sdbd stalls or closes the connection mid-handshake
    /// (broken pipe / timeout / "remote closed stream"), and even a connection that succeeds can hand
    /// back an empty or truncated reply. The very same "0 getduid" command then succeeds on the next
    /// attempt — which is why the TV-information view often shows a DUID the installer failed to read.
    /// So we retry, dropping the connection between tries (the next call reconnects on a fresh socket),
    /// and treat an empty/invalid reply as retryable, not just a transport error.
    /// </summary>
    public static class TizenDuidReader
    {
        /// <summary>
        /// Reads and validates the TV DUID, retrying up to <paramref name="attempts"/> times.
        /// Returns the validated DUID, or throws <see cref="InvalidOperationException"/> carrying the
        /// last failure reason. Callers that prefer a blank over an exception can catch it.
        /// </summary>
        public static async Task<string> ReadAsync(ISdbEngine sdb, string tvIp, int attempts = 3, Action<string>? progress = null)
        {
            string lastReason = string.Empty;

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                ProcessResult result;
                try
                {
                    result = await sdb.DuidAsync(tvIp).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    lastReason = ex.Message;
                    await SafeDisconnectAsync(sdb, tvIp).ConfigureAwait(false);
                    result = null!;
                }

                if (result is not null)
                {
                    var duid = (result.Output ?? string.Empty)
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault()?.Trim() ?? string.Empty;

                    if (result.ExitCode == 0 && TizenDuid.IsValid(duid))
                        return duid;

                    // ExitCode 0 with a bad DUID = an empty/truncated reply from a slow TV; a non-zero
                    // exit carries the transport error. Both are retryable, so drop and try again.
                    lastReason = !string.IsNullOrWhiteSpace(result.Error) ? result.Error
                        : string.IsNullOrEmpty(duid) ? "empty response"
                        : $"unexpected response '{duid}'";
                    await SafeDisconnectAsync(sdb, tvIp).ConfigureAwait(false);
                }

                if (attempt < attempts)
                {
                    progress?.Invoke($"Reading TV DUID… (retry {attempt + 1}/{attempts})");
                    await Task.Delay(500).ConfigureAwait(false); // let the old TV's sdbd settle
                }
            }

            throw new InvalidOperationException(
                $"Could not read a valid TV DUID{(string.IsNullOrWhiteSpace(lastReason) ? "." : $": {lastReason}")}");
        }

        private static async Task SafeDisconnectAsync(ISdbEngine sdb, string tvIp)
        {
            try { await sdb.DisconnectAsync(tvIp).ConfigureAwait(false); }
            catch { /* best effort — the next read reconnects regardless */ }
        }
    }
}
