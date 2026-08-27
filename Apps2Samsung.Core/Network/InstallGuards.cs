using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;

namespace Apps2Samsung.Services
{
    /// <summary>What a pre-install guard is warning about.</summary>
    public enum InstallGuardKind
    {
        /// <summary>Developer Mode is on but its install service isn't running yet.</summary>
        RestartRequired,
        /// <summary>The TV's Developer-Mode IP is on a different subnet than this machine.</summary>
        SubnetMismatch,
        /// <summary>The TV reports Developer Mode off.</summary>
        DeveloperModeOff,
        /// <summary>The TV's Developer-Mode IP is this machine's IP typed back to front.</summary>
        DeveloperIpReversed,
        /// <summary>The TV's Developer-Mode IP points at some other machine.</summary>
        DeveloperIpMismatch,
    }

    /// <summary>
    /// One thing that will most likely make the install fail, phrased for the user. Every guard is a
    /// Continue/Stop confirmation, never a hard block — a TV can always be misreporting.
    ///
    /// <see cref="MessageKey"/> / <see cref="TitleKey"/> are the desktop head's localization keys;
    /// <see cref="DefaultMessage"/> / <see cref="DefaultTitle"/> carry the same English text for the
    /// mobile head, which has no localization layer (same split as
    /// <see cref="TizenDeviceReadiness.RestartInstructions"/>). <see cref="Detail"/> is the measured
    /// facts behind the guard (IP addresses) — not translatable prose, so both heads append it as-is.
    /// </summary>
    public sealed record InstallGuard(
        InstallGuardKind Kind,
        string TitleKey,
        string MessageKey,
        string DefaultTitle,
        string DefaultMessage,
        string? Detail = null)
    {
        /// <summary>The English message with the measured facts appended (what the mobile head shows).</summary>
        public string DefaultMessageWithDetail =>
            string.IsNullOrEmpty(Detail) ? DefaultMessage : $"{DefaultMessage}\n\n{Detail}";
    }

    /// <summary>What the guards are evaluated against — supplied by each head from its own settings.</summary>
    public sealed class InstallGuardOptions
    {
        /// <summary>Every local IP this machine/phone answers on. Empty means "unknown": the
        /// IP-matching guards are then skipped rather than guessed at.</summary>
        public IReadOnlyCollection<string> LocalIps { get; init; } = Array.Empty<string>();

        /// <summary>The local IP the user picked (desktop NIC selection) or the phone's own IP.
        /// Only used for the subnet comparison; empty skips it.</summary>
        public string? ConfiguredLocalIp { get; init; }

        /// <summary>The desktop's "Reverse IP (for Arabic/Hebrew)" setting: those TVs render the IP
        /// right-to-left, so the user types it reversed and it is then <i>correct</i>, not a mismatch.</summary>
        public bool ReversedIpReading { get; init; }
    }

    /// <summary>The guards to show, plus the TV IP to install to if evaluation corrected it.</summary>
    public sealed record InstallGuardResult(IReadOnlyList<InstallGuard> Guards, string? CorrectedTvIp)
    {
        public static readonly InstallGuardResult None = new(Array.Empty<InstallGuard>(), null);
    }

    /// <summary>
    /// The pre-install checks both heads run before touching a TV: Developer Mode off, a
    /// Developer-Mode IP pointing at another machine (or typed back to front), a TV on another subnet,
    /// and a TV that still needs a restart. Each one is the real reason an install would otherwise die
    /// somewhere deep in the DUID read or the SDB connect, with an error nobody can act on.
    ///
    /// Pure classification — no dialogs here. Each head renders <see cref="InstallGuard"/>s its own
    /// way (localized Avalonia dialogs on desktop, DisplayAlert on mobile) so the two agree on when to
    /// warn and what to say.
    /// </summary>
    public static class InstallGuards
    {
        // Same English text as the desktop's en.json entries for these keys, so both heads say the
        // same thing. Keep the two in sync when either changes.
        private const string RestartTitleDefault = TizenDeviceReadiness.RestartTitle;
        private const string SubnetMismatchDefault = "Devices are in different subnets (network)";
        private const string DeveloperModeRequiredDefault = "Samsung TV is not in developer mode...";
        private const string DeveloperIpReversedDefault =
            "IP is in reversed order on the TV, please re-enable developer mode and type your local IP " +
            "in reversed order. (ex: 192.168.1.2 → 2.1.168.192)";
        private const string DeveloperIpMismatchDefault =
            "Samsung Developer mode IP doesn't match this devices local IP, do you wish to continue?";

        /// <summary>
        /// Evaluates every guard for <paramref name="device"/>, in the order they should be shown.
        /// An empty list means nothing looks wrong. Returns no guards for a device whose
        /// Developer-Mode info was never read (<c>DeveloperIP == null</c>) — there is nothing to
        /// compare against then.
        /// </summary>
        public static InstallGuardResult Evaluate(NetworkDevice? device, InstallGuardOptions options, INetworkService network)
        {
            if (device is null || device.DeveloperIP is null)
                return InstallGuardResult.None;

            var guards = new List<InstallGuard>();
            string? correctedTvIp = null;

            // 1. Developer Mode on, but its install service isn't up yet — only a restart fixes it.
            if (TizenDeviceReadiness.RequiresRestart(device))
            {
                guards.Add(new InstallGuard(
                    InstallGuardKind.RestartRequired,
                    "restartRequiredTitle", "restartRequiredBody",
                    RestartTitleDefault, TizenDeviceReadiness.RestartInstructions));
            }

            // 2. TV on another subnet than the local IP the user selected.
            if (!string.IsNullOrEmpty(options.ConfiguredLocalIp) &&
                !string.IsNullOrEmpty(device.DeveloperIP) &&
                network.IsDifferentSubnet(options.ConfiguredLocalIp!, device.DeveloperIP!))
            {
                guards.Add(new InstallGuard(
                    InstallGuardKind.SubnetMismatch,
                    "guardSubnetMismatchTitle", "subnetMismatch",
                    "Subnet Mismatch", SubnetMismatchDefault,
                    $"TV Developer Mode IP: {device.DeveloperIP} • this device: {options.ConfiguredLocalIp}"));
            }

            // 3. The TV itself says Developer Mode is off.
            if (device.DeveloperMode == "0")
            {
                guards.Add(new InstallGuard(
                    InstallGuardKind.DeveloperModeOff,
                    "guardDeveloperModeTitle", "DeveloperModeRequired",
                    "Developer Disabled", DeveloperModeRequiredDefault));
            }

            // 4/5. Developer Mode points at a host that isn't us. Without a local IP to compare
            // against (offline phone, no usable interface) we can't tell, so we don't guess.
            var localIps = options.LocalIps;
            if (localIps.Count == 0 || string.IsNullOrEmpty(device.DeveloperIP))
                return new InstallGuardResult(guards, correctedTvIp);

            bool ipMismatch = !localIps.Contains(device.DeveloperIP);
            bool isReversedIp = ipMismatch && localIps.Any(ip => Invert(network, ip) == device.DeveloperIP);

            if (isReversedIp && options.ReversedIpReading)
            {
                // Reading the IP right-to-left is exactly what this user's TV asks of them, so the
                // reversed value is the correct one — install to it instead of warning.
                ipMismatch = false;
                correctedTvIp = device.DeveloperIP;
            }
            else if (isReversedIp)
            {
                guards.Add(new InstallGuard(
                    InstallGuardKind.DeveloperIpReversed,
                    "guardIpReversedTitle", "DeveloperIPReversed",
                    "IP Reversed", DeveloperIpReversedDefault,
                    $"TV Developer Mode IP: {device.DeveloperIP}"));
                ipMismatch = false;
            }

            if (ipMismatch)
            {
                guards.Add(new InstallGuard(
                    InstallGuardKind.DeveloperIpMismatch,
                    "guardIpMismatchTitle", "DeveloperIPMismatch",
                    "IP Mismatch", DeveloperIpMismatchDefault,
                    $"TV Developer Mode IP: {device.DeveloperIP} • this device: {string.Join(", ", localIps)}"));
            }

            return new InstallGuardResult(guards, correctedTvIp);
        }

        // InvertIPAddress throws on anything that isn't four dot-separated parts; a local IP that odd
        // simply can't be the reversed one.
        private static string? Invert(INetworkService network, string ip)
        {
            try
            {
                return network.InvertIPAddress(ip);
            }
            catch (FormatException ex)
            {
                Trace.WriteLine($"[Guards] Can't reverse local IP '{ip}': {ex.Message}");
                return null;
            }
        }
    }
}
