using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Remote
{
    /// <summary>One app the TV reports as installed, over the remote channel.</summary>
    /// <param name="AppId">The Tizen id — what a launch is addressed to.</param>
    /// <param name="AppType">
    /// 2 for a store app (launched as a deep link), 4 for a native one. Sets that don't report it come
    /// back 0, and the launcher then guesses from the shape of the id.
    /// </param>
    /// <param name="IsLocked">The TV marks the app as locked (a hotel launcher hiding it, or a PIN).</param>
    public sealed record SamsungRemoteInstalledApp(
        string AppId,
        string Name,
        int AppType,
        string? IconUrl = null,
        bool IsLocked = false)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? AppId : Name;
    }

    /// <summary>Which of the launch paths got the app open.</summary>
    public enum SamsungRemoteLaunchRoute
    {
        None,
        /// <summary><c>ed.apps.launch</c> over the remote channel.</summary>
        Channel,
        /// <summary><c>POST /api/v2/applications/{id}</c>.</summary>
        Rest,
        /// <summary>DIAL on port 8080 — by app name, not by id.</summary>
        Dial,

        /// <summary>
        /// <c>0 was_execute</c> over SDB. The only route that addresses the platform's own launcher
        /// rather than the store's, and the only one that needs Developer Mode (#641).
        /// </summary>
        Sdb,
    }

    /// <summary>
    /// What a launch attempt came to. <paramref name="Verified"/> separates "the TV says the app is
    /// running" from "the TV took the message and never said what happened" — a distinction worth
    /// keeping, because plenty of firmware answers neither the status query nor with an error.
    /// </summary>
    public sealed record SamsungRemoteLaunchResult(
        bool Succeeded,
        SamsungRemoteLaunchRoute Route,
        bool Verified)
    {
        public static readonly SamsungRemoteLaunchResult Failed =
            new(false, SamsungRemoteLaunchRoute.None, false);
    }

    /// <summary>
    /// One row of the toolbox's launcher list: an app that can be tried by id, whether or not the TV
    /// admits to having it.
    /// </summary>
    /// <param name="ReportedByTv">
    /// The TV listed this app itself. False means it came from the community catalogue only — either
    /// the set doesn't answer the installed-app query, or it is hiding the app, which is exactly the
    /// case this feature exists for. Launching one of those is a "try it and see".
    /// </param>
    public sealed record SamsungRemoteLaunchTarget(
        string AppId,
        string Name,
        string? IconUrl,
        int AppType,
        bool ReportedByTv);

    /// <summary>
    /// Lists and launches the TV's apps by id (#635). That is the point of it: hospitality firmware
    /// ships the OTT apps and the hotel launcher merely hides them, so launching one by id reaches an
    /// app the on-screen UI won't show. The same holds for regional and pre-installed apps on an
    /// ordinary set.
    /// <para>
    /// Firmware coverage varies wildly, so a launch is tried every way this app can reach a TV: the
    /// channel's <c>ed.apps.launch</c>, the REST endpoint, DIAL, and — where the caller has an
    /// <see cref="ISdbEngine"/> — <c>0 was_execute</c> over SDB. The first three need no Developer
    /// Mode, which is why they lead for an ordinary app; SDB leads for a
    /// <see cref="SamsungSystemApps">system app</see>, which was never in a store for a deep link or a
    /// DIAL name to address. Each attempt is checked against the TV's own app-status endpoint where the
    /// set answers it; where it doesn't, a delivered message is reported as an unverified success
    /// rather than claimed as a launch.
    /// </para>
    /// <para>
    /// Listing still goes over the channel alone. <c>Sdb/TizenInstalledApps</c> is the other listing —
    /// it needs Developer Mode and reports what is really installed, where this one is a catalogue of
    /// ids to try.
    /// </para>
    /// </summary>
    public static class SamsungRemoteApps
    {
        private const int RestPort = 8001;
        private const int DialPort = 8080;

        private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(8);

        // Every request here is to a LAN address the user picked, and a TV that is thinking about a
        // launch shouldn't hold the UI for HttpClient's 100s default.
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

        /// <summary>
        /// Asks the TV for its installed apps. Empty when the set doesn't implement the query — several
        /// do not, and they answer by staying silent rather than refusing, so this is a wait that times
        /// out rather than an error.
        /// </summary>
        public static async Task<IReadOnlyList<SamsungRemoteInstalledApp>> ListInstalledAsync(
            SamsungRemoteClient client,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);

            var reply = await client.RequestAsync("ed.installedApp.get", data: null, ListTimeout, cancellationToken)
                .ConfigureAwait(false);

            // The payload is nested twice: the channel's data envelope, then the event's own.
            if (reply?["data"]?["data"] is not JsonArray items)
            {
                Trace.WriteLine("[remote] the TV did not answer the installed-app query.");
                return Array.Empty<SamsungRemoteInstalledApp>();
            }

            var apps = new List<SamsungRemoteInstalledApp>(items.Count);
            foreach (var item in items)
            {
                var appId = item?["appId"]?.ToString();
                if (string.IsNullOrWhiteSpace(appId))
                    continue;

                apps.Add(new SamsungRemoteInstalledApp(
                    AppId: appId,
                    Name: item?["name"]?.ToString() ?? string.Empty,
                    AppType: ToInt(item?["app_type"]),
                    IconUrl: NullIfBlank(item?["icon"]?.ToString()),
                    IsLocked: item?["is_lock"]?.ToString() is "1" or "true"));
            }

            return apps
                .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The list the toolbox shows, before the TV has said anything: every app in the community
        /// catalogue, ready to launch by id.
        /// <para>
        /// This is the primary source, not a fallback. <c>ed.installedApp.get</c> was dropped from
        /// Tizen somewhere around 2020 — older sets answer it, most current ones never reply — and a
        /// hospitality set hides its apps from the launcher precisely when you need one. A list that
        /// waited for the TV would therefore be empty exactly in the case this feature exists for, so
        /// the catalogue renders first and <see cref="Merge"/> folds in whatever the set volunteers.
        /// </para>
        /// </summary>
        public static async Task<IReadOnlyList<SamsungRemoteLaunchTarget>> CatalogueTargetsAsync(
            CancellationToken cancellationToken = default) =>
            Merge(await Catalog.SamsungTvAppCatalog.GetAsync(cancellationToken).ConfigureAwait(false), reported: null);

        /// <summary>
        /// Merges what the TV reported with the community catalogue, by app id. Pure, so a head can
        /// re-run it when a late answer arrives without re-fetching anything.
        /// </summary>
        public static IReadOnlyList<SamsungRemoteLaunchTarget> Merge(
            IReadOnlyList<Catalog.SamsungTvApp> catalogue,
            IReadOnlyList<SamsungRemoteInstalledApp>? reported)
        {
            ArgumentNullException.ThrowIfNull(catalogue);

            // Icons: what the TV calls an icon is a path inside its own filesystem, so the catalogue's
            // store URL is the icon wherever it knows the app.
            var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in catalogue)
            {
                if (string.IsNullOrWhiteSpace(app.IconUrl))
                    continue;
                foreach (var id in app.Ids)
                    icons[id] = app.IconUrl;
            }

            var targets = new List<SamsungRemoteLaunchTarget>(catalogue.Count + (reported?.Count ?? 0));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var app in reported ?? Array.Empty<SamsungRemoteInstalledApp>())
            {
                if (!seen.Add(app.AppId))
                    continue;

                targets.Add(new SamsungRemoteLaunchTarget(
                    AppId: app.AppId,
                    Name: app.DisplayName,
                    IconUrl: icons.GetValueOrDefault(app.AppId) ?? HttpIconOrNull(app.IconUrl),
                    AppType: app.AppType,
                    ReportedByTv: true));
            }

            foreach (var app in catalogue)
            {
                // Any of an app's ids already listed means the TV reported it under one of them.
                if (app.Ids.Any(seen.Contains))
                    continue;

                seen.Add(app.Id);
                targets.Add(new SamsungRemoteLaunchTarget(
                    AppId: app.Id,
                    Name: app.Name,
                    IconUrl: string.IsNullOrWhiteSpace(app.IconUrl) ? null : app.IconUrl,
                    AppType: 0,
                    ReportedByTv: false));
            }

            return targets
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>Launches a row of the list, name and all.</summary>
        public static Task<SamsungRemoteLaunchResult> LaunchAsync(
            SamsungRemoteClient? client,
            string tvIpAddress,
            SamsungRemoteLaunchTarget target,
            ISdbEngine? sdb = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(target);
            return LaunchAsync(client, tvIpAddress, target.AppId, target.Name, target.AppType, sdb, cancellationToken);
        }

        /// <summary>
        /// Opens <paramref name="appId"/> on the TV, trying the channel, then REST, then DIAL, with SDB
        /// either side of them depending on the app. <paramref name="dialName"/> is the app's
        /// registered DIAL name (e.g. "Netflix") — DIAL addresses apps by name, so without one that
        /// fallback is skipped. <paramref name="sdb"/> is null where the caller has no engine, and a
        /// set with Developer Mode off simply fails that attempt and carries on.
        /// </summary>
        public static async Task<SamsungRemoteLaunchResult> LaunchAsync(
            SamsungRemoteClient? client,
            string tvIpAddress,
            string appId,
            string? dialName = null,
            int appType = 0,
            ISdbEngine? sdb = null,
            CancellationToken cancellationToken = default)
        {
            // Null client is a route missing, not a bad call: only the first of the four needs the
            // channel, and a set that refuses to pair can still answer REST, DIAL or SDB.
            if (string.IsNullOrWhiteSpace(appId))
                return SamsungRemoteLaunchResult.Failed;

            appId = appId.Trim();

            // A hotel or factory menu is not a store app, so a deep link and a DIAL name have nothing
            // to address it by: for those, SDB is the route rather than the fallback.
            var sdbFirst = SamsungSystemApps.IsSystemApp(appId);
            if (sdbFirst && await SdbLaunchedAsync(sdb, tvIpAddress, appId).ConfigureAwait(false))
                return await SdbResultAsync(tvIpAddress, appId, cancellationToken).ConfigureAwait(false);

            // Tizen suspends an app rather than closing it, so a set that ran this app earlier still
            // answers the status query "true" before we have launched anything. Left alone that reads
            // as an instant verified success on every relaunch while the screen never changes, which
            // is what a hospitality set was seen doing: an app opened once could not be opened again
            // until the TV restarted. Close it first so the status query means something again.
            if (await IsRunningAsync(tvIpAddress, appId, cancellationToken).ConfigureAwait(false) == true)
            {
                await TerminateAsync(tvIpAddress, appId, cancellationToken).ConfigureAwait(false);
                await WaitForStoppedAsync(tvIpAddress, appId, cancellationToken).ConfigureAwait(false);
            }

            // 1. The channel. It reports delivery only, so the TV's own status endpoint is what turns
            //    that into a launch — where the set answers it at all.
            if (client is not null && await client.EmitAsync("ed.apps.launch", new JsonObject
                {
                    ["appId"] = appId,
                    ["action_type"] = ActionTypeFor(appId, appType),
                }, cancellationToken).ConfigureAwait(false))
            {
                var running = await WaitForRunningAsync(tvIpAddress, appId, cancellationToken).ConfigureAwait(false);
                if (running == true)
                    return new SamsungRemoteLaunchResult(true, SamsungRemoteLaunchRoute.Channel, Verified: true);

                // The set doesn't answer the status query: the message went out and nothing contradicts
                // it, so stop here rather than firing two more launches at a TV that may be opening the
                // app right now.
                if (running is null)
                    return new SamsungRemoteLaunchResult(true, SamsungRemoteLaunchRoute.Channel, Verified: false);
            }

            // 2. REST. A set that answered the status query above answers this too, and some firmware
            //    honours it where the channel event is ignored.
            if (await PostAsync($"http://{tvIpAddress}:{RestPort}/api/v2/applications/{Uri.EscapeDataString(appId)}", cancellationToken)
                .ConfigureAwait(false))
            {
                var running = await WaitForRunningAsync(tvIpAddress, appId, cancellationToken).ConfigureAwait(false);
                return new SamsungRemoteLaunchResult(true, SamsungRemoteLaunchRoute.Rest, Verified: running == true);
            }

            // 3. DIAL, the pre-Tizen path some sets still serve. By name only.
            if (!string.IsNullOrWhiteSpace(dialName) &&
                await PostAsync($"http://{tvIpAddress}:{DialPort}/ws/apps/{Uri.EscapeDataString(dialName.Trim())}", cancellationToken)
                    .ConfigureAwait(false))
            {
                return new SamsungRemoteLaunchResult(true, SamsungRemoteLaunchRoute.Dial, Verified: false);
            }

            // 4. SDB, for an ordinary app the three network routes couldn't open. Last because it costs
            //    a connection to the TV's developer port to find out it isn't available.
            if (!sdbFirst && await SdbLaunchedAsync(sdb, tvIpAddress, appId).ConfigureAwait(false))
                return await SdbResultAsync(tvIpAddress, appId, cancellationToken).ConfigureAwait(false);

            return SamsungRemoteLaunchResult.Failed;
        }

        /// <summary>
        /// Whether the developer channel took the launch. False whenever that channel isn't there to
        /// ask — no engine, Developer Mode off, the TV not listening on the port — which is a route
        /// unavailable rather than an error, so the caller falls through to the next one.
        /// </summary>
        private static async Task<bool> SdbLaunchedAsync(ISdbEngine? sdb, string tvIpAddress, string appId)
        {
            if (sdb is null)
                return false;

            try
            {
                var result = await sdb.LaunchAsync(tvIpAddress, appId).ConfigureAwait(false);
                if (result.ExitCode == 0)
                    return true;

                Trace.WriteLine($"[remote] SDB would not open {appId}: {result.Output}");
                return false;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[remote] SDB launch of {appId} failed: {ex.Message}");
                return false;
            }
        }

        // SDB reports the launcher's own answer, which is a better signal than any of the network
        // routes give — but a system app is exactly the kind the status endpoint stays quiet about, so
        // it still goes through the same check as the rest rather than claiming a verified launch.
        private static async Task<SamsungRemoteLaunchResult> SdbResultAsync(
            string tvIpAddress, string appId, CancellationToken cancellationToken)
        {
            var running = await WaitForRunningAsync(tvIpAddress, appId, cancellationToken).ConfigureAwait(false);
            return new SamsungRemoteLaunchResult(true, SamsungRemoteLaunchRoute.Sdb, Verified: running == true);
        }

        /// <summary>
        /// Whether the TV reports the app as running: true/false when it answered, null when it doesn't
        /// serve the status endpoint (a 404, or no answer at all) and the question can't be settled.
        /// </summary>
        public static async Task<bool?> IsRunningAsync(string tvIpAddress, string appId, CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await Http
                    .GetAsync($"http://{tvIpAddress}:{RestPort}/api/v2/applications/{Uri.EscapeDataString(appId)}", cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                // Reported as a bool by most firmware and as "true"/"false" by some.
                var running = JsonNode.Parse(json)?["running"]?.ToString();
                return bool.TryParse(running, out var isRunning) ? isRunning : null;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[remote] status of {appId} on {tvIpAddress} is unknown: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Asks the TV to close <paramref name="appId"/>. False when the set doesn't serve the
        /// endpoint, which several do not — a relaunch is then still worth attempting.
        /// </summary>
        public static Task<bool> TerminateAsync(string tvIpAddress, string appId, CancellationToken cancellationToken = default) =>
            DeleteAsync($"http://{tvIpAddress}:{RestPort}/api/v2/applications/{Uri.EscapeDataString(appId)}", cancellationToken);

        // Closing is not instant either. Waiting matters more than the result: launching while the old
        // instance is still going is the case this whole detour exists to avoid.
        private static async Task WaitForStoppedAsync(string tvIpAddress, string appId, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

                if (await IsRunningAsync(tvIpAddress, appId, cancellationToken).ConfigureAwait(false) != true)
                    return;
            }
        }

        // An app takes a moment to come up, so a single immediate query would read "not running" on a
        // launch that is working. Poll briefly; give up as soon as the set shows it doesn't answer.
        private static async Task<bool?> WaitForRunningAsync(string tvIpAddress, string appId, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken).ConfigureAwait(false);

                var running = await IsRunningAsync(tvIpAddress, appId, cancellationToken).ConfigureAwait(false);
                if (running != false)
                    return running;
            }

            return false;
        }

        private static async Task<bool> PostAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await Http.PostAsync(url, content: null, cancellationToken).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[remote] POST {url} failed: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> DeleteAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await Http.DeleteAsync(url, cancellationToken).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[remote] DELETE {url} failed: {ex.Message}");
                return false;
            }
        }

        // Store apps are deep links; native ones (org.tizen.*, and anything else non-numeric) are not.
        // The TV tells us which via app_type where it lists its apps; a hand-typed id has to be read
        // from its shape.
        private static string ActionTypeFor(string appId, int appType) =>
            appType == 4 || (appType == 0 && !appId.All(char.IsDigit))
                ? "NATIVE_LAUNCH"
                : "DEEP_LINK";

        // app_type comes back as a number on most sets and as a string on some.
        private static int ToInt(JsonNode? node) =>
            node is not null && int.TryParse(node.ToString(), out var value) ? value : 0;

        private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        // What the TV calls an icon is usually a path on the TV's own filesystem; only an absolute URL
        // is something a UI here can show.
        private static string? HttpIconOrNull(string? icon) =>
            !string.IsNullOrWhiteSpace(icon) && icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? icon
                : null;
    }
}
