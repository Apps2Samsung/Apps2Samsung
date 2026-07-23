using System;
using System.Net.Http;
using System.Threading.Tasks;
using Apps2Samsung.Helpers.API;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Mobile.Services;

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
		_loaded = true;

		RefreshSignInState();
	}

	private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

	private void OnServerUrlUnfocused(object? sender, FocusEventArgs e)
	{
		if (_loaded)
			MobileSettings.JellyfinServerUrl = ServerUrlEntry.Text?.Trim() ?? string.Empty;
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
			SetStatus("Enter a server URL first.", isError: true);
			return;
		}
		if (string.IsNullOrWhiteSpace(username))
		{
			SetStatus("Enter a username to sign in (or just leave the server URL set).", isError: true);
			return;
		}

		SignInBtn.IsEnabled = false;
		SetStatus("Connecting…", isError: false);
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
			SetStatus($"Sign-in failed: {ex.Message}", isError: true);
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
