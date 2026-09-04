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
    /// which reported 474 installed packages against the handful its launcher shows. Its full
    /// <c>getAppsInfo()</c> dump confirms every id here is installed there, the two wizard ids
    /// included (<c>com.samsung.tv.wizard</c> and <c>org.tizen.wizard</c> are both present; a
    /// different set's dump has only the latter). They are not promises: ids move between
    /// generations, and a set that never had the app will simply say so. Nothing here defeats a
    /// Security Mode PIN — these open the menu, they do not unlock it.
    /// </para>
    /// <para>
    /// How they are reached, settled by that set's log: <b>not</b> over SDB and not over the remote
    /// channel. <c>0 was_execute</c> is the Smart Hub launcher — it resolves an id against the Smart
    /// Hub app database (what <c>vd_applist</c> prints) and answers <c>launch failed[400]</c> for
    /// anything else, and none of these is a Smart Hub app; web or native makes no difference. The
    /// channel and REST go through the same launcher and add a "try again" toast. What does reach them
    /// is the platform's application manager, from inside a sideloaded app — which is what
    /// <see cref="Agent.DebugAgentClient">the debug agent</see> is for. These rows therefore launch
    /// through the agent when it is attached; without it the SDB attempt still runs, so the user at
    /// least sees the TV's verdict instead of a toast.
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
