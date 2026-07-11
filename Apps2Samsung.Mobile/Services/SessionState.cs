using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Process-lived state shared across pages so the user signs in to Samsung only once per app run.
/// </summary>
public sealed class SessionState
{
	public SamsungAuth? Auth { get; set; }

	public bool IsSignedIn => Auth is not null && !string.IsNullOrEmpty(Auth.access_token);
}
