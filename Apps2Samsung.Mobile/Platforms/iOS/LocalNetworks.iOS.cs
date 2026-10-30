using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// iOS: nothing enumerated, so the scanner keeps its single socket-derived seed (the phone's /24).
/// The personal hotspot case is a separate story with its own interface API (#920 is Android).
/// </summary>
public static partial class LocalNetworks
{
    private static partial IEnumerable<LocalNetwork> EnumeratePlatform() => Array.Empty<LocalNetwork>();
}
