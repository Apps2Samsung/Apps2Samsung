using System;
using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Models;
using Apps2Samsung.Sdb;
using Apps2Samsung.Services;

namespace Apps2Samsung.Design
{
    /// <summary>The status LED on the TV card's screen.</summary>
    public enum TvLed
    {
        /// <summary>No TV: the LED is dark.</summary>
        Off,
        /// <summary>Green, glowing: Developer Mode on and the debug port open, so installable.</summary>
        Ok,
        /// <summary>Amber: found on the network but not installable yet; the message says why.</summary>
        Warn,
        /// <summary>Red: unreachable.</summary>
        Bad,
        /// <summary>Accent, pulsing: scanning, downloading, signing or installing.</summary>
        Busy,
    }

    /// <summary>How this device reaches the TV's install service.</summary>
    public enum TvRoute
    {
        /// <summary>sdbd on port 26101, the TV's Host PC IP pointing at this device.</summary>
        Direct,
        /// <summary>TV-Store's relay on the TV (Host PC IP 127.0.0.1, TV-Store#52).</summary>
        Relay,
    }

    /// <summary>
    /// A chip on the TV card's screen while the TV isn't ready: what the user can do next. The heads
    /// label them through <see cref="TvCardStates.ActionLabelKey"/> and run them; the card only lists.
    /// </summary>
    public enum TvCardAction
    {
        /// <summary>Open the Developer Mode guide (Developer Mode off, or its Host PC IP wrong).</summary>
        FixDeveloperMode,
        /// <summary>Open the guide's relay steps (Host PC IP 127.0.0.1, relay not answering).</summary>
        TurnOnRelay,
        /// <summary>Re-read the TV: Developer Mode fields, debug port, relay port.</summary>
        CheckAgain,
        /// <summary>The user says the TV was restarted: re-read it.</summary>
        Restarted,
    }

    /// <summary>
    /// What the TV card shows (#755, "the hero"): the TV drawn as a device, with its name, an IP and
    /// model subline, a status line behind an LED, and a progress bar while something is running.
    /// Both heads build this from the same inputs through <see cref="TvCardStates"/> and only draw
    /// it, so the two can't tell the same TV a different story.
    /// </summary>
    /// <param name="Name">The TV's name, or what stands in for one ("No TV selected").</param>
    /// <param name="Subline">Model and IP, or the hint on how to get a TV found.</param>
    /// <param name="Led">Which LED.</param>
    /// <param name="Message">The status line next to the LED; empty hides the line.</param>
    /// <param name="Progress">The TV's install percentage while it reports one, else null.</param>
    /// <param name="IsDevice">True when a real TV is on the screen: the heads play the "power on"
    /// animation when this flips to true for a new name.</param>
    /// <param name="Route">How installs reach the TV; the heads show a pill only for <see cref="TvRoute.Relay"/>.</param>
    /// <param name="RelayDown">The route is the relay and its port does not answer: the pill turns amber.</param>
    /// <param name="Problem">What keeps the TV from accepting installs, as the Developer Mode guide sees it.</param>
    /// <param name="Actions">The chips to offer under the status line, the primary one first; empty for a ready TV.</param>
    public sealed record TvCardState(
        string Name,
        string Subline,
        TvLed Led,
        string Message,
        int? Progress = null,
        bool IsDevice = false,
        TvRoute Route = TvRoute.Direct,
        bool RelayDown = false,
        DevModeProblem Problem = DevModeProblem.None,
        IReadOnlyList<TvCardAction>? Actions = null)
    {
        public IReadOnlyList<TvCardAction> Actions { get; init; } = Actions ?? Array.Empty<TvCardAction>();
        public bool ViaRelay => Route == TvRoute.Relay;
        public bool HasActions => Actions.Count > 0;
    }

    /// <summary>The card states, built from a localizer so each head's catalog supplies the words.</summary>
    public static class TvCardStates
    {
        /// <summary>Nothing selected and nothing found: how to get a TV on the screen.</summary>
        public static TvCardState NoTv(Func<string, string> localize, string? localIp) => new(
            localize("tvCardNoTv"),
            string.IsNullOrEmpty(localIp)
                ? localize("tvCardNoTvHint")
                : string.Format(localize("tvCardNoTvHintIp"), localIp),
            TvLed.Off,
            string.Empty);

        /// <summary>A scan is running.</summary>
        public static TvCardState Scanning(Func<string, string> localize) => new(
            localize("tvCardSearching"),
            string.Empty,
            TvLed.Busy,
            localize("statusScanningForTvs"));

        /// <summary>
        /// A TV, judged the way the Developer Mode guide judges it (<see cref="DevModeGuide.Diagnose"/>),
        /// so the card never says "ready" for a TV the guide would call broken: green when Developer
        /// Mode is on, points at this device (or at TV-Store's relay, which answers) and the install
        /// service is up; otherwise amber with the guide's own status line and the chips that lead to
        /// the fix. <paramref name="localIps"/> are every address this device answers on (empty skips
        /// the IP comparison, as the guide does); <paramref name="localIp"/> is the one to name in a
        /// message, on a phone the address the TV sees (#920).
        /// </summary>
        public static TvCardState ForDevice(NetworkDevice device, IReadOnlyCollection<string> localIps, string? localIp, Func<string, string> localize)
        {
            var name = string.IsNullOrWhiteSpace(device.DeviceName) ? localize("tvCardSamsungTv") : device.DeviceName;
            var subline = string.IsNullOrWhiteSpace(device.ModelName)
                ? device.IpAddress
                : $"{device.ModelName} · {device.IpAddress}";

            var ips = WithLocal(localIps, localIp);
            var problem = DevModeGuide.Diagnose(device, ips);
            var route = SdbEndpoint.IsLoopback(device.DeveloperIP) ? TvRoute.Relay : TvRoute.Direct;
            var relayDown = problem == DevModeProblem.RelayDown;

            var message = problem switch
            {
                DevModeProblem.None => localize(route == TvRoute.Relay ? "tvCardReadyRelay" : "tvCardReady"),
                DevModeProblem.RelayDown => localize("tvCardRelayDown"),
                _ => DevModeGuide.Status(problem, device, localIp ?? ips.FirstOrDefault(), localize),
            };

            return new TvCardState(name, subline,
                problem == DevModeProblem.None ? TvLed.Ok : TvLed.Warn,
                message, IsDevice: true, Route: route, RelayDown: relayDown, Problem: problem,
                Actions: ActionsFor(problem));
        }

        /// <summary>The older shape: one local address, which is also the list to compare against.</summary>
        public static TvCardState ForDevice(NetworkDevice device, string? localIp, Func<string, string> localize)
            => ForDevice(device, Array.Empty<string>(), localIp, localize);

        /// <summary>The chips for a problem: the way to the fix first, a re-check where waiting is the fix.</summary>
        public static IReadOnlyList<TvCardAction> ActionsFor(DevModeProblem problem) => problem switch
        {
            DevModeProblem.DeveloperModeOff or DevModeProblem.IpMismatch or DevModeProblem.IpReversed
                => new[] { TvCardAction.FixDeveloperMode },
            DevModeProblem.RelayDown => new[] { TvCardAction.TurnOnRelay, TvCardAction.CheckAgain },
            DevModeProblem.RestartRequired => new[] { TvCardAction.Restarted },
            _ => Array.Empty<TvCardAction>(),
        };

        /// <summary>The en.json key of a chip's label.</summary>
        public static string ActionLabelKey(TvCardAction action) => action switch
        {
            TvCardAction.FixDeveloperMode => "tvCardActionFixDevMode",
            TvCardAction.TurnOnRelay => "tvCardActionShowHow",
            TvCardAction.CheckAgain => "tvCardActionCheckAgain",
            _ => "tvCardActionRestarted",
        };

        /// <summary>The route pill's text: "via TV-Store relay", or "relay off" while it does not answer; empty for a direct TV.</summary>
        public static string RouteText(TvCardState state, Func<string, string> localize) =>
            state.Route != TvRoute.Relay ? string.Empty
            : localize(state.RelayDown ? "tvCardRelayOff" : "tvCardViaRelay");

        /// <summary>
        /// A TV as one row of a list (the TV picker): the LED colour and a few words on where it
        /// stands, "via relay" for a ready relay TV, "points at 192.168.1.20" for a wrong Host PC IP.
        /// Empty text for a ready TV on a direct connection.
        /// </summary>
        public static (TvLed Led, string Text) RowStatus(NetworkDevice device, IReadOnlyCollection<string> localIps, string? localIp, Func<string, string> localize)
        {
            var state = ForDevice(device, localIps, localIp, localize);
            var text = state.Problem switch
            {
                DevModeProblem.None => state.Route == TvRoute.Relay ? localize("tvRowViaRelay") : string.Empty,
                DevModeProblem.DeveloperModeOff => localize("tvRowDevModeOff"),
                DevModeProblem.IpMismatch or DevModeProblem.IpReversed => string.Format(localize("tvRowPointsAt"), device.DeveloperIP),
                DevModeProblem.RelayDown => localize("tvRowRelayOff"),
                _ => localize("tvRowRestart"),
            };
            return (state.Led, text);
        }

        // The named address belongs in the comparison too: a phone on a hotspot names the hotspot
        // address, which the interface list may not carry (#920).
        private static IReadOnlyCollection<string> WithLocal(IReadOnlyCollection<string> localIps, string? localIp)
        {
            if (string.IsNullOrEmpty(localIp) || localIps.Contains(localIp))
                return localIps;
            var list = new List<string>(localIps) { localIp };
            return list;
        }
    }
}
