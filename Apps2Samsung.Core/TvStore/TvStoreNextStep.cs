using System;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.TvStore
{
    /// <summary>What is left to do on the TV once TV-Store is armed (#960).</summary>
    public enum TvStoreNextStep
    {
        /// <summary>Host PC IP still names this device: TV-Store needs the TV's own address or 127.0.0.1, then a restart.</summary>
        ChangeHostIp,
        /// <summary>Host PC IP is 127.0.0.1: TV-Store installs over loopback and this app goes through its relay.</summary>
        UsingRelay,
        /// <summary>Host PC IP already is the address TV-Store connects on; nothing to change.</summary>
        Done,
    }

    /// <summary>
    /// Picks the closing message of Arm TV-Store from the Host PC IP the TV reports right after the
    /// arm. People skip a step they are only told about, so the message says what is actually left.
    /// </summary>
    public static class TvStoreNextSteps
    {
        /// <param name="hostPcIp">Developer Mode's Host PC IP as the TV reports it now; null or empty when it could not be read.</param>
        /// <param name="ownIp">The address TV-Store connects to its own sdbd on, from its status.</param>
        public static TvStoreNextStep For(string? hostPcIp, string? ownIp)
        {
            if (SdbEndpoint.IsLoopback(hostPcIp))
                return TvStoreNextStep.UsingRelay;

            return !string.IsNullOrWhiteSpace(hostPcIp) && !string.IsNullOrWhiteSpace(ownIp) &&
                   string.Equals(hostPcIp.Trim(), ownIp.Trim(), StringComparison.Ordinal)
                ? TvStoreNextStep.Done
                : TvStoreNextStep.ChangeHostIp;
        }

        /// <summary>The localization key of the closing message; each takes the TV-Store version and the TV's own address.</summary>
        public static string MessageKey(TvStoreNextStep step) => step switch
        {
            TvStoreNextStep.UsingRelay => "lblToolboxTvStoreArmedRelay",
            TvStoreNextStep.Done => "lblToolboxTvStoreArmedDone",
            _ => "lblToolboxTvStoreArmed",
        };
    }
}
