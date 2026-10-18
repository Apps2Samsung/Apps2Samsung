using System;
using Apps2Samsung.Models;
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
    public sealed record TvCardState(
        string Name,
        string Subline,
        TvLed Led,
        string Message,
        int? Progress = null,
        bool IsDevice = false);

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
        /// A TV. Ready when its debug port answers; otherwise amber with the reason a person can act
        /// on (Developer Mode off, its Developer-Mode IP pointing elsewhere, a power cycle needed).
        /// </summary>
        public static TvCardState ForDevice(NetworkDevice device, string? localIp, Func<string, string> localize)
        {
            var name = string.IsNullOrWhiteSpace(device.DeviceName) ? localize("tvCardSamsungTv") : device.DeviceName;
            var subline = string.IsNullOrWhiteSpace(device.ModelName)
                ? device.IpAddress
                : $"{device.ModelName} · {device.IpAddress}";

            if (!device.DebugPortOpen)
            {
                var reason = TizenDeviceReadiness.WhyNotReady(device, localIp);
                return new TvCardState(name, subline, TvLed.Warn,
                    TizenDeviceReadiness.Describe(reason, localIp, localize), IsDevice: true);
            }

            return new TvCardState(name, subline, TvLed.Ok, localize("tvCardReady"), IsDevice: true);
        }
    }
}
