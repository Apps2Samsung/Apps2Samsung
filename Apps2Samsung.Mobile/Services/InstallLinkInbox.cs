using Apps2Samsung.Catalog;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Install links (#1066) reaching the phone app: Android's activity and iOS's scene delegate hand
/// every apps2samsung:// link in here. A link that comes before the Install tab is up waits for it
/// (<see cref="Subscribe"/>); a newer one replaces one not yet taken.
/// </summary>
public static class InstallLinkInbox
{
	private static readonly object Gate = new();
	private static string? _pending;
	private static Action<string>? _handler;

	public static void Deliver(string? link)
	{
		if (!InstallLink.LooksLikeLink(link))
			return;
		Action<string>? handler;
		lock (Gate)
		{
			handler = _handler;
			if (handler is null)
				_pending = link;
		}
		System.Diagnostics.Trace.WriteLine("[links] link received");
		if (handler is not null)
			MainThread.BeginInvokeOnMainThread(() => handler(link!));
	}

	/// <summary>The Install tab's handler; a link that came before it is passed on at once.</summary>
	public static void Subscribe(Action<string> handler)
	{
		string? pending;
		lock (Gate)
		{
			_handler = handler;
			pending = _pending;
			_pending = null;
		}
		if (pending is not null)
			MainThread.BeginInvokeOnMainThread(() => handler(pending));
	}
}
