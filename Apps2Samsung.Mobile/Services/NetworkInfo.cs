using System.Net;
using System.Net.Sockets;
using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Services;

/// <summary>Small helpers for the phone's own network identity.</summary>
public static class NetworkInfo
{
	/// <summary>
	/// The phone's LAN IPv4, via the route the OS picks for an outbound socket (no packets are sent —
	/// Connect on a UDP socket just resolves the local endpoint). Works on Android where interface
	/// enumeration/masks don't. Returns null when offline.
	/// </summary>
	public static string? GetLocalIPv4()
	{
		try
		{
			using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
			socket.Connect("8.8.8.8", 65530);
			return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// The phone's IP as the TV at <paramref name="tvIp"/> sees it: the address on the local network
	/// that contains the TV (the hotspot address for a TV on the phone's hotspot, the Wi-Fi address
	/// for a TV on the home LAN), else <see cref="GetLocalIPv4"/>. This is what goes next to a TV on
	/// screen and into the TV's Host PC IP field (#920): with a hotspot on, the default-route address
	/// is the cellular one, which the TV can never reach.
	/// </summary>
	public static string? LocalIpFor(string? tvIp) =>
		LocalNetwork.LocalIpFor(LocalNetworks.Enumerate(), tvIp) ?? GetLocalIPv4();
}
