using System;
using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Models;

namespace Apps2Samsung.Design
{
    /// <summary>What keeps a TV from accepting installs, as the Developer Mode guide sees it.</summary>
    public enum DevModeProblem
    {
        /// <summary>Nothing: Developer Mode is on, points here, and the install service is up.</summary>
        None,
        DeveloperModeOff,
        /// <summary>Developer Mode points at another machine.</summary>
        IpMismatch,
        /// <summary>Developer Mode points at this machine's IP typed back to front.</summary>
        IpReversed,
        /// <summary>Developer Mode looks right, but the TV hasn't been restarted since.</summary>
        RestartRequired,
    }

    /// <summary>One numbered step of the guide. <see cref="Needed"/> false = greyed, already done.</summary>
    public sealed record DevModeGuideStep(int Number, string TitleKey, string HintKey, bool Needed, bool ShowsLocalIp);

    /// <summary>
    /// The Developer Mode guide (#755): the five steps that turn a Samsung TV into one that accepts
    /// installs, with the ones that still matter for this TV highlighted, and a status line saying
    /// what is wrong right now. Pure: both heads render it and re-run <see cref="Diagnose"/> after
    /// each re-read of the TV.
    /// </summary>
    public static class DevModeGuide
    {
        private static readonly (string Title, string Hint)[] StepKeys =
        {
            ("devGuideStepOpenApps", "devGuideStepOpenAppsHint"),
            ("devGuideStepTypeCode", "devGuideStepTypeCodeHint"),
            ("devGuideStepSwitchOn", "devGuideStepSwitchOnHint"),
            ("devGuideStepEnterIp", "devGuideStepEnterIpHint"),
            ("devGuideStepRestart", "devGuideStepRestartHint"),
        };

        /// <summary>
        /// What is wrong with <paramref name="device"/>. <paramref name="localIps"/> are the addresses
        /// this machine answers on; empty skips the IP comparison rather than guessing.
        /// </summary>
        public static DevModeProblem Diagnose(NetworkDevice? device, IReadOnlyCollection<string> localIps)
        {
            if (device is null)
                return DevModeProblem.None;

            // The emulator has no REST API, so its Developer-Mode fields are never read; an open debug
            // port is then all there is to go on.
            var off = device.DeveloperMode == "0" || (device.DeveloperMode is null && !device.DebugPortOpen);
            if (off)
                return DevModeProblem.DeveloperModeOff;

            if (!string.IsNullOrEmpty(device.DeveloperIP) && localIps.Count > 0 && !localIps.Contains(device.DeveloperIP))
            {
                return localIps.Any(ip => Reverse(ip) == device.DeveloperIP)
                    ? DevModeProblem.IpReversed
                    : DevModeProblem.IpMismatch;
            }

            return device.DebugPortOpen ? DevModeProblem.None : DevModeProblem.RestartRequired;
        }

        /// <summary>The five steps, with the ones <paramref name="problem"/> still needs marked.</summary>
        public static IReadOnlyList<DevModeGuideStep> Steps(DevModeProblem problem)
        {
            int[] needed = problem switch
            {
                DevModeProblem.DeveloperModeOff => new[] { 1, 2, 3, 4, 5 },
                DevModeProblem.IpMismatch or DevModeProblem.IpReversed => new[] { 1, 2, 4, 5 },
                DevModeProblem.RestartRequired => new[] { 5 },
                _ => Array.Empty<int>(),
            };
            return StepKeys
                .Select((k, i) => new DevModeGuideStep(i + 1, k.Title, k.Hint, needed.Contains(i + 1), ShowsLocalIp: i == 3))
                .ToList();
        }

        public static string TitleKey(DevModeProblem problem) => problem switch
        {
            DevModeProblem.DeveloperModeOff => "devGuideTitleOff",
            DevModeProblem.IpMismatch or DevModeProblem.IpReversed => "devGuideTitleIp",
            DevModeProblem.RestartRequired => "devGuideTitleRestart",
            _ => "devGuideTitleReady",
        };

        /// <summary>The status line under the title, through the head's localization.</summary>
        public static string Status(DevModeProblem problem, NetworkDevice? device, string? localIp, Func<string, string> localize) => problem switch
        {
            DevModeProblem.DeveloperModeOff => localize("devGuideStatusOff"),
            DevModeProblem.IpMismatch => string.IsNullOrEmpty(localIp)
                ? string.Format(localize("devGuideStatusIpMismatchNoLocal"), device?.DeveloperIP)
                : string.Format(localize("devGuideStatusIpMismatch"), device?.DeveloperIP, localIp),
            DevModeProblem.IpReversed => string.Format(localize("devGuideStatusIpReversed"), device?.DeveloperIP, localIp),
            DevModeProblem.RestartRequired => localize("devGuideStatusRestart"),
            _ => localize("devGuideStatusReady"),
        };

        private static string? Reverse(string ip)
        {
            var parts = ip.Split('.');
            return parts.Length == 4 ? string.Join(".", Enumerable.Reverse(parts)) : null;
        }
    }
}
