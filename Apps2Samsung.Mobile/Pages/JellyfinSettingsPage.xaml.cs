using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Apps2Samsung.Helpers.API;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Jellyfin;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Mobile.Localization;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// Settings → Jellyfin. Sets the server URL and (optionally) signs in so the shared
/// <c>JellyfinPackagePatcher</c> can bake the server address + auto-login credentials + custom CSS
/// into a Jellyfin .wgt at install. Mirrors the desktop Jellyfin settings, minus the desktop-only
/// bits (server scripts / plugin patches, dev-logs, multi-user admin, playback prefs). Values persist
/// eagerly to <see cref="MobileSettings"/>, matching the other mobile settings pages.
/// </summary>
public partial class JellyfinSettingsPage : ContentPage
{
	private bool _loaded;

	public JellyfinSettingsPage()
	{
		InitializeComponent();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		_loaded = false;
		ServerUrlEntry.Text = MobileSettings.JellyfinServerUrl;
		CssEditor.Text = MobileSettings.JellyfinCustomCss;
		YoutubeSwitch.IsToggled = MobileSettings.JellyfinPatchYoutube;
		// Populate the theme picker once from the shared Core catalog (same list the desktop uses).
		ThemePicker.ItemsSource ??= JellyThemeCatalog.Themes.Select(t => t.DisplayName).ToList();
		_loaded = true;

		UpdateMdnsWarning(ServerUrlEntry.Text);
		RefreshSignInState();
	}

	// Selecting a community theme replaces the CSS with its @import (mirrors the desktop gallery).
	private void OnThemeSelected(object? sender, EventArgs e)
	{
		if (!_loaded)
			return;
		var i = ThemePicker.SelectedIndex;
		if (i < 0 || i >= JellyThemeCatalog.Themes.Count)
			return;
		var css = JellyThemeCatalog.Themes[i].CssImportStatement;
		CssEditor.Text = css;
		MobileSettings.JellyfinCustomCss = css;
	}

	private void OnClearCss(object? sender, EventArgs e)
	{
		CssEditor.Text = string.Empty;
		MobileSettings.JellyfinCustomCss = string.Empty;
		ThemePicker.SelectedIndex = -1;
	}

	private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

	private void OnServerUrlUnfocused(object? sender, FocusEventArgs e)
	{
		if (_loaded)
			MobileSettings.JellyfinServerUrl = ServerUrlEntry.Text?.Trim() ?? string.Empty;
		UpdateMdnsWarning(ServerUrlEntry.Text);
	}

	// Checks the server is reachable without needing credentials (GETs /System/Info/Public via the
	// shared Core API client), so the user can verify the address before signing in or installing.
	private async void OnTestConnectionClicked(object? sender, EventArgs e)
	{
		var url = UrlHelper.NormalizeServerUrl(ServerUrlEntry.Text?.Trim() ?? string.Empty);
		if (string.IsNullOrWhiteSpace(url))
		{
			ShowTest("Enter a server URL first.", isError: true);
			return;
		}

		TestBtn.IsEnabled = false;
		ShowTest("Testing…", isError: false);
		try
		{
			using var http = new HttpClient();
			var api = new JellyfinApiClient(http);
			var info = await api.GetPublicSystemInfoAsync(url);
			if (info is not null && !string.IsNullOrEmpty(info.Id))
				ShowTest(string.IsNullOrEmpty(info.ServerName) ? "✓ Reachable." : $"✓ Reachable — {info.ServerName}.", isError: false);
			else
				ShowTest("Couldn't reach a Jellyfin server at that address.", isError: true);
		}
		catch (Exception ex)
		{
			ShowTest($"Couldn't reach the server: {ex.Message}", isError: true);
		}
		finally
		{
			TestBtn.IsEnabled = true;
		}
	}

	private void ShowTest(string message, bool isError)
	{
		ServerTestLabel.Text = message;
		ServerTestLabel.TextColor = isError ? Color.FromArgb("#B00020") : Color.FromArgb("#2E7D32");
		ServerTestLabel.Opacity = 1.0;
		ServerTestLabel.IsVisible = true;
	}

	// Warn when the host is an mDNS (.local) name — Tizen TVs resolve these unreliably.
	private void UpdateMdnsWarning(string? url)
	{
		MdnsWarning.IsVisible = IsMdnsHost(url);
	}

	private static bool IsMdnsHost(string? url)
	{
		if (string.IsNullOrWhiteSpace(url))
			return false;
		var s = url.Trim();
		var scheme = s.IndexOf("://", StringComparison.Ordinal);
		if (scheme >= 0) s = s[(scheme + 3)..];
		var slash = s.IndexOf('/');
		if (slash >= 0) s = s[..slash];
		var colon = s.IndexOf(':');
		if (colon >= 0) s = s[..colon];
		return s.TrimEnd('.').EndsWith(".local", StringComparison.OrdinalIgnoreCase);
	}

	private void OnCssUnfocused(object? sender, FocusEventArgs e)
	{
		if (_loaded)
			MobileSettings.JellyfinCustomCss = CssEditor.Text ?? string.Empty;
	}

	private void OnToggle(object? sender, ToggledEventArgs e)
	{
		if (_loaded)
			MobileSettings.JellyfinPatchYoutube = YoutubeSwitch.IsToggled;
	}

	private void OnTogglePasswordVisibility(object? sender, EventArgs e)
	{
		PasswordEntry.IsPassword = !PasswordEntry.IsPassword;
		PwEyeBtn.Opacity = PasswordEntry.IsPassword ? 1.0 : 0.5;
	}

	private async void OnSignInClicked(object? sender, EventArgs e)
	{
		// Persist the latest URL first so the "no server" gate and auth use the same value.
		MobileSettings.JellyfinServerUrl = ServerUrlEntry.Text?.Trim() ?? string.Empty;
		var serverUrl = UrlHelper.NormalizeServerUrl(MobileSettings.JellyfinServerUrl);
		var username = UsernameEntry.Text?.Trim() ?? string.Empty;
		var password = PasswordEntry.Text ?? string.Empty;

		if (string.IsNullOrWhiteSpace(serverUrl))
		{
			SetStatus(L10n.Get("statusEnterServerUrl"), isError: true);
			return;
		}
		if (string.IsNullOrWhiteSpace(username))
		{
			SetStatus(L10n.Get("statusEnterUsername"), isError: true);
			return;
		}

		SignInBtn.IsEnabled = false;
		SetStatus(L10n.Get("ConnectingToDevice"), isError: false);
		try
		{
			// A dedicated client so auth-header mutations never touch the app's shared HttpClient.
			using var http = new HttpClient();
			var api = new JellyfinApiClient(http);

			var (accessToken, userId, isAdmin, error) = await api.AuthenticateAsync(serverUrl, username, password);
			if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(userId))
			{
				SetStatus(error ?? "Sign-in failed. Check the server URL and credentials.", isError: true);
				return;
			}

			await MobileSettings.SetJellyfinAccessTokenAsync(accessToken);
			MobileSettings.JellyfinUserId = userId;

			// Cache the real server GUID/name/LAN address so auto-login won't hit a ServerMismatch.
			var info = await api.GetPublicSystemInfoAsync(serverUrl);
			if (info is not null && !string.IsNullOrEmpty(info.Id))
			{
				MobileSettings.JellyfinServerId = info.Id;
				MobileSettings.JellyfinServerName = info.ServerName ?? string.Empty;
				MobileSettings.JellyfinServerLocalAddress = info.LocalAddress ?? string.Empty;
			}

			RefreshSignInState();
			PasswordEntry.Text = string.Empty;
		}
		catch (Exception ex)
		{
			SetStatus(string.Format(L10n.Get("statusSignInFailed"), ex.Message), isError: true);
		}
		finally
		{
			SignInBtn.IsEnabled = true;
		}
	}

	private async void OnSignOutClicked(object? sender, EventArgs e)
	{
		await MobileSettings.SetJellyfinAccessTokenAsync(null);
		MobileSettings.JellyfinUserId = string.Empty;
		MobileSettings.JellyfinServerId = string.Empty;
		MobileSettings.JellyfinServerName = string.Empty;
		MobileSettings.JellyfinServerLocalAddress = string.Empty;
		RefreshSignInState();
	}

	// Reflects whether saved credentials exist: shows a signed-in banner + the clear button, or a hint.
	private void RefreshSignInState()
	{
		var signedIn = !string.IsNullOrEmpty(MobileSettings.JellyfinAccessToken)
					   && !string.IsNullOrEmpty(MobileSettings.JellyfinUserId);
		if (signedIn)
		{
			var name = MobileSettings.JellyfinServerName;
			SetStatus(string.IsNullOrEmpty(name)
				? "Signed in — auto-login will be baked into the package."
				: $"Signed in to {name} — auto-login will be baked into the package.", isError: false);
		}
		else
		{
			StatusLabel.IsVisible = false;
		}
		SignOutBtn.IsVisible = signedIn;
	}

	private void SetStatus(string message, bool isError)
	{
		System.Diagnostics.Trace.WriteLine($"[jellyfin] {message}");
		StatusLabel.Text = message;
		StatusLabel.TextColor = isError ? Color.FromArgb("#B00020") : Color.FromArgb("#2E7D32");
		StatusLabel.Opacity = 1.0;
		StatusLabel.IsVisible = true;
	}
}
