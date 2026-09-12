using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Remote
{
    /// <summary>What has to be true of the TV before a sequence stands a chance.</summary>
    public enum SamsungRemoteSequencePrecondition
    {
        /// <summary>The set only has to be awake and on the network.</summary>
        Awake,

        /// <summary>
        /// The set must be awake and showing a live TV source. Kept for sequences that genuinely need
        /// the tuner in front; <c>hotel-option</c> turned out not to be one of them, so do not reach
        /// for this without having watched a set refuse the sequence from another source.
        /// </summary>
        LiveTv,

        /// <summary>
        /// The set must be in standby — which no sequence sent over this channel can satisfy: a
        /// sleeping Samsung TV serves neither the REST API nor the remote channel, and waking it with
        /// <see cref="SamsungRemoteWake"/> is precisely what takes it out of standby. Sequences marked
        /// this way are recorded here for reference and are not offered as buttons
        /// (<see cref="SamsungRemoteSequence.CanSendOverNetwork"/>); they need the physical remote.
        /// </summary>
        Standby,
    }

    /// <summary>
    /// One documented key combination, e.g. the hotel-menu combo from Samsung's hospitality
    /// installation manuals. <see cref="NameKey"/>, <see cref="DescriptionKey"/> and
    /// <see cref="CaveatKey"/> are localization keys both heads resolve through the shared catalog.
    /// </summary>
    /// <param name="HoldMs">
    /// How long each key is held. 0 — what every sequence below uses — sends a plain click; a positive
    /// value presses, waits, and releases (<see cref="SamsungRemoteClient.SendKeyPressAsync"/>), for a
    /// set that reacts to a held button rather than a tap.
    /// </param>
    public sealed record SamsungRemoteSequence(
        string Id,
        string NameKey,
        string DescriptionKey,
        IReadOnlyList<string> Keys,
        SamsungRemoteSequencePrecondition Precondition,
        string? CaveatKey = null,
        int HoldMs = 0)
    {
        /// <summary>
        /// False for the standby combos: the channel doesn't exist while the set is asleep, so a button
        /// for one could never fire. The UI says so instead of offering it.
        /// </summary>
        public bool CanSendOverNetwork => Precondition != SamsungRemoteSequencePrecondition.Standby;
    }

    /// <summary>One key of a sequence, and whether the channel took it.</summary>
    public sealed record SamsungRemoteKeyDelivery(int Index, string Key, bool Delivered);

    /// <summary>What came of sending a sequence — per key, in the order they went out.</summary>
    public sealed record SamsungRemoteSequenceResult(
        SamsungRemoteSequence Sequence,
        IReadOnlyList<SamsungRemoteKeyDelivery> Deliveries)
    {
        /// <summary>Every key was delivered. It does not mean the TV acted on them — nothing on this
        /// channel reports that; only the screen does.</summary>
        public bool Completed => Deliveries.Count == Sequence.Keys.Count && Deliveries.All(d => d.Delivered);

        public int DeliveredCount => Deliveries.Count(d => d.Delivered);

        /// <summary>The key the run stopped on, or null when it ran to the end.</summary>
        public string? FailedKey => Deliveries.FirstOrDefault(d => !d.Delivered)?.Key;
    }

    /// <summary>
    /// The documented service-menu combinations, and the sender that walks one through the remote
    /// channel with a controlled gap between presses (#635).
    /// <para>
    /// Why over the network at all: these combos want ~5 presses inside a couple of seconds, which a
    /// flaky IR path — or a Bluetooth Smart Remote with no number pad — can't deliver, while this
    /// channel can, repeatably. The set has to be awake and on the network; no Developer Mode is
    /// involved.
    /// </para>
    /// <para>
    /// Only combinations that appear in Samsung's own installation manuals are listed. Nothing here
    /// brute-forces a Security Mode PIN or bypasses a lock, and the UI says the tool is for a set you
    /// own.
    /// </para>
    /// <para>
    /// Unknown, until it is tried on real hardware, is whether the TV recognises these combos when the
    /// keys arrive over the network at all: the detection may sit in the IR/micom path, below where the
    /// WebSocket injects. Delivery is therefore all this reports, per key.
    /// </para>
    /// </summary>
    public static class SamsungRemoteSequences
    {
        /// <summary>Gap between presses when the caller doesn't pick one.</summary>
        public const int DefaultGapMs = 250;

        /// <summary>Slowest gap a sequence still counts as "one combo" at.</summary>
        public const int MaxGapMs = 600;

        /// <summary>Fastest gap worth sending: below this the TV starts dropping presses.</summary>
        public const int MinGapMs = 100;

        /// <summary>Every documented combination, including the ones this channel cannot send.</summary>
        public static readonly IReadOnlyList<SamsungRemoteSequence> All = new ReadOnlyCollection<SamsungRemoteSequence>(
            new[]
            {
                new SamsungRemoteSequence(
                    Id: "hotel-option",
                    NameKey: "lblToolboxSeqHotelOption",
                    DescriptionKey: "lblToolboxSeqHotelOptionDesc",
                    Keys: new[]
                    {
                        SamsungRemoteKeys.Mute,
                        SamsungRemoteKeys.Digit(1),
                        SamsungRemoteKeys.Digit(1),
                        SamsungRemoteKeys.Digit(9),
                        SamsungRemoteKeys.Enter,
                    },
                    // #635 assumed this needed the tuner in front. It doesn't: sent over the network to
                    // a UE55RU7020 sitting on HDMI1, it opened the Hotel Option menu straight away.
                    Precondition: SamsungRemoteSequencePrecondition.Awake),

                new SamsungRemoteSequence(
                    Id: "hotel-option-documented",
                    NameKey: "lblToolboxSeqHotelOptionDoc",
                    DescriptionKey: "lblToolboxSeqHotelOptionDocDesc",
                    Keys: new[]
                    {
                        SamsungRemoteKeys.Mute,
                        SamsungRemoteKeys.Up,
                        SamsungRemoteKeys.Down,
                        SamsungRemoteKeys.Enter,
                    },
                    // This is the sequence Samsung's own hospitality manual gives (HU7000F/HU8000F
                    // installation guide, "Setting the Hotel Option menu"). The 1-1-9 combos above are
                    // the general Samsung ones and are not what these sets document, so try this first
                    // on anything with an HG model number.
                    Precondition: SamsungRemoteSequencePrecondition.Awake),

                new SamsungRemoteSequence(
                    Id: "hotel-option-alt",
                    NameKey: "lblToolboxSeqHotelOptionAlt",
                    DescriptionKey: "lblToolboxSeqHotelOptionAltDesc",
                    Keys: new[]
                    {
                        SamsungRemoteKeys.Mute,
                        SamsungRemoteKeys.Digit(1),
                        SamsungRemoteKeys.Digit(1),
                        SamsungRemoteKeys.Digit(9),
                        SamsungRemoteKeys.Power,
                    },
                    Precondition: SamsungRemoteSequencePrecondition.Awake,
                    // The last press is POWER: on a set that ignores the combo it simply switches off.
                    CaveatKey: "lblToolboxSeqPowerCaveat"),

                new SamsungRemoteSequence(
                    Id: "factory-standby",
                    NameKey: "lblToolboxSeqFactory",
                    DescriptionKey: "lblToolboxSeqFactoryDesc",
                    Keys: new[]
                    {
                        SamsungRemoteKeys.Mute,
                        SamsungRemoteKeys.Digit(1),
                        SamsungRemoteKeys.Digit(8),
                        SamsungRemoteKeys.Digit(2),
                        SamsungRemoteKeys.Power,
                    },
                    Precondition: SamsungRemoteSequencePrecondition.Standby),

                new SamsungRemoteSequence(
                    Id: "service-standby",
                    NameKey: "lblToolboxSeqService",
                    DescriptionKey: "lblToolboxSeqServiceDesc",
                    Keys: new[]
                    {
                        SamsungRemoteKeys.Info,
                        SamsungRemoteKeys.Menu,
                        SamsungRemoteKeys.Mute,
                        SamsungRemoteKeys.Power,
                    },
                    Precondition: SamsungRemoteSequencePrecondition.Standby),
            });

        /// <summary>The combinations this channel can actually deliver — what the UI offers as buttons.</summary>
        public static IReadOnlyList<SamsungRemoteSequence> Sendable { get; } =
            new ReadOnlyCollection<SamsungRemoteSequence>(All.Where(s => s.CanSendOverNetwork).ToList());

        /// <summary>
        /// The combinations that start from standby. No button can ever fire one, but they are the
        /// sequences most likely to rescue a genuinely locked set, so the UI prints them as
        /// instructions for the physical remote instead of hiding them (#639).
        /// <para>
        /// Why they matter: the Hotel Option menu can be switched off outright (<c>Menu OSD &gt; Menu
        /// Display: OFF</c>, which installers routinely apply), and that kills every
        /// <c>hotel-option</c> combination above while leaving the lower-level Service Menu reachable.
        /// </para>
        /// </summary>
        public static IReadOnlyList<SamsungRemoteSequence> StandbyOnly { get; } =
            new ReadOnlyCollection<SamsungRemoteSequence>(All.Where(s => !s.CanSendOverNetwork).ToList());

        /// <summary>
        /// How to enter one of <see cref="StandbyOnly"/> by hand, in order, as en.json keys both heads
        /// resolve. Here rather than in either head's markup so the two print the same steps.
        /// <para>
        /// The detail that decides whether it works is step 3: the whole combination has to go in
        /// inside about three seconds, and speed is what people get wrong.
        /// </para>
        /// </summary>
        public static readonly IReadOnlyList<string> StandbyStepKeys = new ReadOnlyCollection<string>(
            new[]
            {
                "lblToolboxStandbyStep1",
                "lblToolboxStandbyStep2",
                "lblToolboxStandbyStep3",
                "lblToolboxStandbyStep4",
                "lblToolboxStandbyStep5",
            });

        public static SamsungRemoteSequence? Find(string id) =>
            All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Walks <paramref name="sequence"/> through the channel, waiting <paramref name="gapMs"/>
        /// between presses (clamped to <see cref="MinGapMs"/>..<see cref="MaxGapMs"/> — the timing is
        /// the whole point of sending these over the network, so it stays adjustable for a fussy set).
        /// Stops at the first press the channel refuses, because the rest of a half-delivered combo
        /// only leaves the TV in a state nobody asked for. <paramref name="progress"/> sees each key as
        /// it goes out.
        /// </summary>
        public static async Task<SamsungRemoteSequenceResult> SendAsync(
            SamsungRemoteClient client,
            SamsungRemoteSequence sequence,
            int gapMs = DefaultGapMs,
            IProgress<SamsungRemoteKeyDelivery>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(sequence);

            var gap = Math.Clamp(gapMs, MinGapMs, MaxGapMs);
            var deliveries = new List<SamsungRemoteKeyDelivery>(sequence.Keys.Count);

            for (var i = 0; i < sequence.Keys.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key = sequence.Keys[i];
                var delivered = await SendOneAsync(client, key, sequence.HoldMs, cancellationToken).ConfigureAwait(false);

                var delivery = new SamsungRemoteKeyDelivery(i, key, delivered);
                deliveries.Add(delivery);
                progress?.Report(delivery);

                if (!delivered)
                {
                    Trace.WriteLine($"[remote] sequence {sequence.Id} stopped at key {i + 1}/{sequence.Keys.Count} ({key}).");
                    break;
                }

                // No trailing wait: the combo ends with its last press.
                if (i < sequence.Keys.Count - 1)
                    await Task.Delay(gap, cancellationToken).ConfigureAwait(false);
            }

            return new SamsungRemoteSequenceResult(sequence, deliveries);
        }

        private static async Task<bool> SendOneAsync(SamsungRemoteClient client, string key, int holdMs, CancellationToken cancellationToken)
        {
            if (holdMs <= 0)
                return await client.SendKeyAsync(key, cancellationToken).ConfigureAwait(false);

            if (!await client.SendKeyPressAsync(key, cancellationToken).ConfigureAwait(false))
                return false;

            try
            {
                await Task.Delay(holdMs, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // A held key repeats until it is let go, so release it even on cancellation.
                await client.SendKeyReleaseAsync(key, CancellationToken.None).ConfigureAwait(false);
            }

            return true;
        }
    }
}
