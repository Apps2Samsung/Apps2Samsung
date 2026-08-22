using System.Diagnostics;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Turns an exception into something a user can act on <i>and</i> something a maintainer can debug.
/// <para>
/// Android failures here are often Java exceptions raised inside a platform call (the file picker
/// resolving a <c>content://</c> URI, for instance). Those frequently carry a null Java message, so
/// <c>ex.Message</c> alone degrades to the framework's generic "Exception of type 'X' was thrown" —
/// and in a trimmed Release build, to the bare resource key <c>Exception_WasThrown</c>. Either way the
/// dialog said nothing. So: name the type when there's no real message, unwrap the inner chain, and
/// always write the FULL exception (for a <c>Java.Lang.Throwable</c> that includes the Java stack
/// trace, which names the platform API that actually failed) to the session log that
/// Settings → Diagnostics → "Share debug log" hands off.
/// </para>
/// </summary>
public static class ErrorText
{
	/// <summary>
	/// Logs <paramref name="ex"/> in full under <paramref name="context"/> and returns a short
	/// description for the UI.
	/// </summary>
	public static string Describe(Exception ex, string context)
	{
		// ex.ToString(), not ex.Message: this is the only place the Java stack trace is preserved.
		Trace.WriteLine($"[{context}] {ex}");

		var parts = new List<string>();
		for (Exception? e = ex; e is not null && parts.Count < 3; e = e.InnerException)
		{
			var message = e.Message?.Trim();

			// No message, or the stripped-resource-key form ("Exception_WasThrown, Java.Lang.X") that
			// a trimmed build produces for one — the type name is strictly more informative.
			if (string.IsNullOrEmpty(message) ||
				message.StartsWith("Exception_WasThrown", StringComparison.Ordinal))
				message = e.GetType().FullName ?? e.GetType().Name;

			if (!parts.Contains(message))
				parts.Add(message);
		}

		return string.Join(" → ", parts);
	}
}
