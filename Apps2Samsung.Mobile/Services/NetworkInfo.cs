using System.Net;
using System.Net.Sockets;

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
}
