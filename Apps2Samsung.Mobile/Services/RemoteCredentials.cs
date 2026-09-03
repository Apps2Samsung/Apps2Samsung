using Apps2Samsung.Remote;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// This head's side of Core's <see cref="RemoteSession"/>: where the pairing token and the TV's MAC
/// are kept (MAUI Preferences, via <see cref="MobileSettings"/>), and what this head says when a
/// connection ends short of an open channel.
/// </summary>
public sealed class RemoteCredentials : IRemoteCredentialStore
{
	public static readonly RemoteCredentials Instance = new();

	public string? GetToken(string tvIpAddress) => MobileSettings.GetRemoteToken(tvIpAddress);
	public void SetToken(string tvIpAddress, string token) => MobileSettings.SetRemoteToken(tvIpAddress, token);
	public string? GetMac(string tvIpAddress) => MobileSettings.GetRemoteMac(tvIpAddress);
	public void SetMac(string tvIpAddress, string macAddress) => MobileSettings.SetRemoteMac(tvIpAddress, macAddress);

	/// <summary>
	/// The en.json key for a failed connection. The desktop head words the "never seen this TV awake"
	/// case slightly differently, which is why the mapping lives per head rather than in Core.
	/// </summary>
	public static string StatusKeyFor(RemoteSessionOutcome outcome) => outcome switch
	{
		RemoteSessionOutcome.NoMacToWake => "lblRemoteNoAnswerNoMac",
		RemoteSessionOutcome.WakeFailed => "lblRemoteWakeFailed",
		RemoteSessionOutcome.PairingRefused => "lblRemotePairFailed",
		_ => "lblRemoteNoChannel",
	};
}
