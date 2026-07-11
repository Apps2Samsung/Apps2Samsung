using Microsoft.Maui.Graphics;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>Row shown in the device list. Only debug-ready TVs can be installed to.</summary>
public sealed class DeviceItem
{
	public required string IpAddress { get; init; }
	public required string Name { get; init; }
	public required bool IsReady { get; init; }

	public string Badge => IsReady ? "● ready" : "○ not ready";
	public Color BadgeColor => IsReady ? Colors.MediumSeaGreen : Colors.Orange;
}
