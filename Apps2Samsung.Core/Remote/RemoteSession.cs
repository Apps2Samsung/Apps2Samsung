using System;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Remote
{
    /// <summary>
    /// Where a head keeps the two things the remote channel can't rediscover on its own: the pairing
    /// token (without it the TV re-prompts every session) and the MAC captured while the set was awake
    /// (without it a sleeping set can't be woken). The desktop head stores them in settings.json, the
    /// mobile head in Preferences. Both are plain string maps; <see cref="RemoteSession"/> decides
    /// what the key is. The MAC is filed under the IP, because it is looked up when the set is asleep
    /// and the IP is all that's known. The token is filed under the set's own id from the probe
    /// (falling back to the IP on a set that reports none), so a new DHCP lease doesn't cost the
    /// user another pairing prompt (#545).
    /// </summary>
    public interface IRemoteCredentialStore
    {
        string? GetToken(string tvKey);
        void SetToken(string tvKey, string token);
        string? GetMac(string tvIpAddress);
        void SetMac(string tvIpAddress, string macAddress);
    }

    /// <summary>How far <see cref="RemoteSession.ConnectAsync"/> got.</summary>
    public enum RemoteSessionOutcome
    {
        /// <summary>The channel is open.</summary>
        Connected,

        /// <summary>The TV isn't answering and no MAC was ever captured, so it can't be woken either.</summary>
        NoMacToWake,

        /// <summary>A magic packet went out and the TV still didn't come up.</summary>
        WakeFailed,

        /// <summary>
        /// The TV prompted and the prompt wasn't accepted (declined, or nobody was there). Also the
        /// outcome when a set prompted <i>again</i> despite a stored token and that went unanswered.
        /// </summary>
        PairingRefused,

        /// <summary>The set answers on the network but wouldn't open the remote channel.</summary>
        NoChannel,
    }

    /// <param name="TvName">The TV's own friendly name when it reported one, else empty.</param>
    /// <param name="WasFirstPairing">
    /// True when this connection was the one that made the TV show its "allow this device?" prompt —
    /// what separates "you declined it" from "the channel is closed" in a failure message.
    /// </param>
    /// <param name="Capability">
    /// What the probe found out about the set. Carried through because the probe is the only place
    /// some of it is readable — <see cref="SamsungRemoteCapability.IsHospitality"/> in particular,
    /// which a screen wants to say something about (#639). Unsupported when the set never answered.
    /// </param>
    public sealed record RemoteSessionResult(
        RemoteSessionOutcome Outcome,
        SamsungRemoteClient? Client,
        string TvName,
        bool WasFirstPairing,
        SamsungRemoteCapability Capability)
    {
        public bool Connected => Outcome == RemoteSessionOutcome.Connected && Client is not null;
    }

    /// <summary>
    /// Opening the remote channel on a TV: probe, wake it if it is asleep, pair on first use, connect.
    /// Every screen that speaks the channel needs exactly this sequence — the remote on both heads, and
    /// the TV toolbox (#635) — so it lives here once rather than being written out per screen.
    /// <para>
    /// The head supplies its own credential store and does its own localization: <paramref name="status"/>
    /// reports en.json <b>keys</b> as the connection progresses, and the failure key is chosen by the
    /// caller from <see cref="RemoteSessionOutcome"/>, since the two heads word one of those cases
    /// differently.
    /// </para>
    /// </summary>
    public static class RemoteSession
    {
        /// <summary>Status keys reported through the progress callback while connecting.</summary>
        public const string StatusConnecting = "lblRemoteConnecting";
        public const string StatusWaking = "lblRemoteWaking";
        public const string StatusPairPrompt = "lblRemotePairPrompt";
        public const string StatusPairPromptAgain = "lblRemotePairPromptAgain";

        // A pairing needs someone to walk to the TV and accept the prompt. That is expected on the
        // first connection; it also happens on every later one when the set's "Access Notification"
        // is left on "Always" rather than "First Time Only" (#545), a setting the API can't read or
        // change. So the wait is the same either way — the socket itself fails fast if the set has
        // gone, and the only thing that hangs a handshake after a good probe is an unanswered prompt.
        private static readonly TimeSpan PairingTimeout = TimeSpan.FromSeconds(60);
        // A token reconnect the TV lets through answers within a second or so. Past this, the set
        // has put its prompt up, and the user should be told what it is and how to stop it.
        private static readonly TimeSpan RepromptGrace = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(40);

        public static async Task<RemoteSessionResult> ConnectAsync(
            string tvIpAddress,
            IRemoteCredentialStore store,
            IProgress<string>? status = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(store);

            status?.Report(StatusConnecting);
            var capability = await SamsungRemoteClient.ProbeAsync(tvIpAddress, cancellationToken).ConfigureAwait(false);

            // A sleeping TV serves neither the REST API nor the remote channel, so "no answer" and
            // "standby" are one situation: nothing works until the set is woken.
            if (!capability.Supported || !capability.IsAwake)
            {
                var mac = store.GetMac(tvIpAddress);
                if (string.IsNullOrEmpty(mac))
                    return Failed(RemoteSessionOutcome.NoMacToWake, capability);

                status?.Report(StatusWaking);
                using var wakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wakeCts.CancelAfter(WakeTimeout + TimeSpan.FromSeconds(5));

                // Wake-on-LAN needs the TV's own network-standby setting on, and a LAN that passes
                // broadcast; neither is something we can check first.
                if (!await SamsungRemoteWake.WakeAndWaitAsync(tvIpAddress, mac, WakeTimeout, wakeCts.Token).ConfigureAwait(false))
                    return Failed(RemoteSessionOutcome.WakeFailed, capability);

                capability = await SamsungRemoteClient.ProbeAsync(tvIpAddress, cancellationToken).ConfigureAwait(false);
                if (!capability.Supported || !capability.IsAwake)
                    return Failed(RemoteSessionOutcome.WakeFailed, capability);
            }

            // Remember the MAC while it is readable — a sleeping TV won't tell us later.
            if (!string.IsNullOrEmpty(capability.MacAddress))
                store.SetMac(tvIpAddress, capability.MacAddress);

            // The token is filed under the set's own id when it reports one, so a TV that moved to
            // another address keeps its pairing. Tokens stored under the IP by earlier builds are
            // picked up once and re-filed.
            var tokenKey = string.IsNullOrEmpty(capability.DeviceId) ? tvIpAddress : capability.DeviceId;
            var stored = store.GetToken(tokenKey);
            if (string.IsNullOrEmpty(stored) && tokenKey != tvIpAddress)
            {
                stored = store.GetToken(tvIpAddress);
                if (!string.IsNullOrEmpty(stored))
                    store.SetToken(tokenKey, stored);
            }

            var client = new SamsungRemoteClient(tvIpAddress, token: stored, secure: capability.UsesToken);
            client.TokenIssued += token => store.SetToken(tokenKey, token);

            // No stored token on a token-auth set means the TV is about to prompt.
            var firstPairing = capability.UsesToken && string.IsNullOrEmpty(stored);
            if (firstPairing)
                status?.Report(StatusPairPrompt);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(PairingTimeout);

            var connect = client.ConnectAsync(connectCts.Token);
            var prompted = firstPairing;
            if (!firstPairing)
            {
                // Should have been silent. If the TV is still holding the handshake after the grace
                // period it has put its prompt up again — say so, and name the setting behind it.
                var first = await Task.WhenAny(connect, Task.Delay(RepromptGrace, connectCts.Token)).ConfigureAwait(false);
                if (first != connect)
                {
                    prompted = true;
                    status?.Report(StatusPairPromptAgain);
                }
            }

            if (!await connect.ConfigureAwait(false))
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return new RemoteSessionResult(
                    prompted ? RemoteSessionOutcome.PairingRefused : RemoteSessionOutcome.NoChannel,
                    Client: null,
                    capability.Name,
                    firstPairing,
                    capability);
            }

            return new RemoteSessionResult(RemoteSessionOutcome.Connected, client, capability.Name, firstPairing, capability);
        }

        private static RemoteSessionResult Failed(RemoteSessionOutcome outcome, SamsungRemoteCapability capability) =>
            new(outcome, Client: null, capability.Name, WasFirstPairing: false, capability);
    }
}
