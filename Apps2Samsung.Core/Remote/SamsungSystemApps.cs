using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Remote
{
    /// <summary>What opening a system app can cost, if it opens.</summary>
    public enum SamsungSystemAppRisk
    {
        /// <summary>A settings screen. Worst case it does nothing.</summary>
        Safe,

        /// <summary>
        /// A factory screen, a reset, or the first-time wizard: it can undo the set's configuration, so
        /// the UI marks it and it sorts last.
        /// </summary>
        Caution,
    }

    /// <summary>
    /// One built-in app addressed by id rather than by a menu — the hotel menu, the factory menu, the
    /// store. <see cref="NameKey"/> and <see cref="DescriptionKey"/> are localization keys both heads
    /// resolve through the shared catalog, as with <see cref="SamsungRemoteSequence"/>.
    /// </summary>
    public sealed record SamsungSystemApp(
        string AppId,
        string NameKey,
        string DescriptionKey,
        SamsungSystemAppRisk Risk = SamsungSystemAppRisk.Safe);

    /// <summary>
    /// The built-in apps a hospitality set hides, by id (<see href="https://github.com/Apps2Samsung/tizen-community-packages/issues/34">tizen-community-packages#34</see>).
    /// <para>
    /// The reason this list exists: the hotel menu is not a firmware mode that a key combination
    /// unlocks, it is an ordinary Tizen app — <c>com.samsung.ep.coba.hotel</c> — and
    /// <c>MUTE&#160;&gt;&#160;1&#160;1&#160;9</c> does nothing more than ask the platform to open it.
    /// A set whose combination has stopped answering still has the app installed, so addressing it by
    /// id skips the remote entirely. Same for the factory menu, the cloning tool and the store.
    /// </para>
    /// <para>
    /// The ids were read off a Samsung HG43U800FAULXL (Tizen 9.0, 2025 "RoseL" hospitality firmware),
    /// which reported 474 installed packages against the handful its launcher shows. They are not
    /// promises: ids move between generations, and a set that never had the app will simply say so.
    /// Nothing here defeats a Security Mode PIN — these open the menu, they do not unlock it.
    /// </para>
    /// <para>
    /// Which transport reaches them is the open question. The remote channel launches store apps by
    /// deep link and is unlikely to open a platform-owned one; SDB's <c>0 was_execute</c> is the
    /// better bet but wants Developer Mode. <see cref="SamsungRemoteApps.LaunchAsync"/> therefore tries
    /// SDB first for these and the channel first for everything else.
    /// </para>
    /// <para>
    /// Untested against real hardware, and one thing to expect: <c>was_execute</c> is the web app
    /// launcher. The CoBA entries here are web apps and should be in its reach; a native menu such as
    /// <c>org.tizen.factory</c> may need a verb we don't have. A row that opens nothing on every set
    /// belongs out of this list, so what comes back from the first hospitality set to try it decides
    /// what stays.
    /// </para>
    /// </summary>
    public static class SamsungSystemApps
    {
        /// <summary>The hotel menu itself — the one every other entry here is a fallback for.</summary>
        public const string HotelMenuAppId = "com.samsung.ep.coba.hotel";

        /// <summary>Every id worth trying, most likely to help first, <see cref="SamsungSystemAppRisk.Caution"/> last.</summary>
        public static readonly IReadOnlyList<SamsungSystemApp> All = new ReadOnlyCollection<SamsungSystemApp>(
            new[]
            {
                new SamsungSystemApp(
                    HotelMenuAppId,
                    "lblSysAppHotelMenu",
                    "lblSysAppHotelMenuDesc"),

                new SamsungSystemApp(
                    "com.samsung.ep.hotel-quicksetting-editor",
                    "lblSysAppHotelQuick",
                    "lblSysAppHotelQuickDesc"),

                new SamsungSystemApp(
                    "org.tizen.ephotel-cloning",
                    "lblSysAppHotelCloning",
                    "lblSysAppHotelCloningDesc"),

                new SamsungSystemApp(
                    "org.tizen.cloning",
                    "lblSysAppCloning",
                    "lblSysAppCloningDesc"),

                new SamsungSystemApp(
                    "com.samsung.tv.store",
                    "lblSysAppStore",
                    "lblSysAppStoreDesc"),

                new SamsungSystemApp(
                    "org.volt.apps",
                    "lblSysAppVoltStore",
                    "lblSysAppVoltStoreDesc"),

                new SamsungSystemApp(
                    "com.samsung.tv.coba.setting",
                    "lblSysAppSettings",
                    "lblSysAppSettingsDesc"),

                new SamsungSystemApp(
                    "org.tizen.MenuEasySetup",
                    "lblSysAppEasySetup",
                    "lblSysAppEasySetupDesc"),

                new SamsungSystemApp(
                    "org.tizen.tv.swu-standalone",
                    "lblSysAppSoftwareUpdate",
                    "lblSysAppSoftwareUpdateDesc"),

                new SamsungSystemApp(
                    "org.tizen.factory",
                    "lblSysAppFactory",
                    "lblSysAppFactoryDesc",
                    SamsungSystemAppRisk.Caution),

                new SamsungSystemApp(
                    "org.tizen.ep-hotel-factory",
                    "lblSysAppHotelFactory",
                    "lblSysAppHotelFactoryDesc",
                    SamsungSystemAppRisk.Caution),

                new SamsungSystemApp(
                    "org.tizen.ep-hotel-security",
                    "lblSysAppHotelSecurity",
                    "lblSysAppHotelSecurityDesc",
                    SamsungSystemAppRisk.Caution),

                new SamsungSystemApp(
                    "org.tizen.smarthub-reset",
                    "lblSysAppSmartHubReset",
                    "lblSysAppSmartHubResetDesc",
                    SamsungSystemAppRisk.Caution),

                new SamsungSystemApp(
                    "com.samsung.tv.wizard",
                    "lblSysAppWizard",
                    "lblSysAppWizardDesc",
                    SamsungSystemAppRisk.Caution),
            });

        private static readonly HashSet<string> Ids =
            new(All.Select(a => a.AppId), StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether an id is one of these — which is what decides the launch order.</summary>
        public static bool IsSystemApp(string? appId) =>
            !string.IsNullOrWhiteSpace(appId) && Ids.Contains(appId.Trim());

        /// <summary>
        /// The list as both heads show it: text already resolved, and the launch target to hand back to
        /// <see cref="SamsungRemoteApps.LaunchAsync(SamsungRemoteClient, string, SamsungRemoteLaunchTarget, ISdbEngine?, System.Threading.CancellationToken)"/>.
        /// Built here rather than per head so the two don't drift.
        /// </summary>
        public static IReadOnlyList<SamsungSystemAppRow> Rows(Func<string, string> localize)
        {
            ArgumentNullException.ThrowIfNull(localize);

            return All
                .Select(a =>
                {
                    var name = localize(a.NameKey);
                    return new SamsungSystemAppRow(
                        AppId: a.AppId,
                        Name: name,
                        Description: localize(a.DescriptionKey),
                        IsCaution: a.Risk == SamsungSystemAppRisk.Caution,
                        // AppType 4 is "native": it keeps the channel from addressing a platform app as
                        // a store deep link on the fallback attempt. No icon — these were never in a
                        // store to have one — and ReportedByTv is false because the whole point is that
                        // the set doesn't list them.
                        Target: new SamsungRemoteLaunchTarget(a.AppId, name, IconUrl: null, AppType: 4, ReportedByTv: false));
                })
                .ToList();
        }
    }

    /// <summary>One row of the system-app list, ready to bind.</summary>
    public sealed record SamsungSystemAppRow(
        string AppId,
        string Name,
        string Description,
        bool IsCaution,
        SamsungRemoteLaunchTarget Target);
}
