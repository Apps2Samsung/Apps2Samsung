using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// The TV the app is working with, shared by the tabs: the Install tab picks it, On the TV and
/// Remote read it when they appear. Process-lived, like <see cref="SessionState"/>.
/// </summary>
public sealed class TvSelection
{
	public string? Ip { get; private set; }
	public string Label { get; private set; } = string.Empty;
	public NetworkDevice? Device { get; private set; }

	public bool HasTv => !string.IsNullOrEmpty(Ip);
	/// <summary>False only for a TV the scan found without an open debug port.</summary>
	public bool DebugPortOpen => Device?.DebugPortOpen ?? true;

	public event Action? Changed;

	public void Set(string? ip, string label, NetworkDevice? device)
	{
		if (ip == Ip && label == Label && ReferenceEquals(device, Device))
			return;
		Ip = ip;
		Label = label;
		Device = device;
		Changed?.Invoke();
	}

	public void Clear() => Set(null, string.Empty, null);
}
