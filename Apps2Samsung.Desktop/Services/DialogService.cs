using Avalonia;
using Apps2Samsung.Styles;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Apps2Samsung.Certificate;
using Apps2Samsung.Helpers;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Extensions;
using System;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Apps2Samsung.Models;
using Apps2Samsung.ViewModels;
using Apps2Samsung.Views;

namespace Apps2Samsung.Services
{
    public class DialogService : IDialogService
    {
        private static IBrush GetThemeBrush(string resourceKey, bool isDarkMode)
        {
            var themeVariant = isDarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
            if (Application.Current?.TryFindResource(resourceKey, themeVariant, out var resource) == true && resource is IBrush brush)
            {
                return brush;
            }
            // Ultimate fallback
            return resourceKey.Contains("Background")
                ? (isDarkMode ? Brushes.Black : Brushes.White)
                : (isDarkMode ? Brushes.White : Brushes.Black);
        }

        private Window? GetMainWindow()
        {
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var w in desktop.Windows)
                {
                    if (w.IsActive) return w;
                }
                return desktop.MainWindow;
            }

            return null;
        }

        private Window CreateStyledDialog(
            string title,
            Control content,
            bool showButtons = false,
            TaskCompletionSource<bool>? tcs = null,
            string yesText = "Yes",
            string noText = "No",
            Action<Button, Button>? onButtonsCreated = null)
        {
            // Get theme from AppSettings
            var isDarkMode = AppSettings.Default.DarkMode;

            var dialog = new Window
            {
                Title = title,
                Width = 420, // max width
                MinWidth = 300,
                MaxWidth = 600,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CornerRadius = new CornerRadius(12),
                SizeToContent = SizeToContent.Height, // dynamic height
                RequestedThemeVariant = isDarkMode ? ThemeVariant.Dark : ThemeVariant.Light
            };

            // Apply FluentTheme
            dialog.Styles.Add(new StyleInclude(new Uri("avares://Apps2Samsung"))
            {
                Source = new Uri("avares://Avalonia.Themes.Fluent/FluentTheme.xaml")
            });

            // Get colors from theme resources (same as main UI)
            var backgroundBrush = GetThemeBrush("SystemControlBackgroundAltHighBrush", isDarkMode);
            var foregroundBrush = GetThemeBrush("SystemControlForegroundBaseHighBrush", isDarkMode);
            dialog.Background = backgroundBrush;

            var mainPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 10,
                Margin = new Thickness(20)
            };

            mainPanel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                Foreground = foregroundBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            });

            // Wrap content in ScrollViewer to handle long messages
            var scrollViewer = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                MaxHeight = 400 // max height before scroll appears
            };

            mainPanel.Children.Add(scrollViewer);

            if (showButtons && tcs != null)
            {
                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 10,
                    Margin = new Thickness(0, 15, 0, 0)
                };

                var yesButton = new Button
                {
                    Content = yesText,
                    Width = 90,
                    Height = 35,
                    Background = DesignTheme.Brush("Accent"),
                    Foreground = DesignTheme.Brush("OnAccent"),
                    CornerRadius = new CornerRadius(8),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };

                var noButton = new Button
                {
                    Content = noText,
                    Width = 90,
                    Height = 35,
                    Background = DesignTheme.Brush("Muted"),
                    Foreground = DesignTheme.Brush("OnAccent"),
                    CornerRadius = new CornerRadius(8),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };

                // Click is raised while the PointerReleased that caused it is still being routed. Closing
                // the window synchronously here disposes its PlatformImpl mid-route, and the rest of that
                // input event then lands on a dead window ("[Control] PlatformImpl is null, couldn't
                // handle input"). Let the event finish first, then close.
                yesButton.Click += (_, _) => { tcs.SetResult(true); Dispatcher.UIThread.Post(dialog.Close); };
                noButton.Click += (_, _) => { tcs.SetResult(false); Dispatcher.UIThread.Post(dialog.Close); };

                buttons.Children.Add(yesButton);
                buttons.Children.Add(noButton);

                mainPanel.Children.Add(buttons);

                // Lets a caller keep hold of the buttons (e.g. to gate "Install now" on a countdown)
                // instead of hunting for them in the visual tree by their label.
                onButtonsCreated?.Invoke(yesButton, noButton);
            }

            dialog.Content = mainPanel;
            return dialog;
        }

        public async Task ShowMessageAsync(string title, string message)
        {
            var window = GetMainWindow();
            var isDarkMode = AppSettings.Default.DarkMode;
            var foregroundBrush = GetThemeBrush("SystemControlForegroundBaseHighBrush", isDarkMode);

            var dialog = CreateStyledDialog(title, new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = foregroundBrush,
                FontSize = 14,
                Margin = new Thickness(0, 5, 0, 0)
            });

            if (window != null)
                await dialog.ShowDialog(window);
        }

        public async Task ShowErrorAsync(string message)
        {
            var window = GetMainWindow();
            Control contentControl;
            
            var logIndex = message.IndexOf("\n[Log written to:");
            if (logIndex >= 0)
            {
                var mainMessage = message.Substring(0, logIndex).Trim();
                var logMessage = message.Substring(logIndex).Trim();

                var stackPanel = new StackPanel { Spacing = 5, Orientation = Orientation.Vertical };
                
                stackPanel.Children.Add(new TextBlock
                {
                    Text = mainMessage,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Red,
                    FontSize = 14,
                    Margin = new Thickness(0, 5, 0, 0)
                });

                var buttonContent = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6
                };
                buttonContent.Children.Add(new FluentAvalonia.UI.Controls.SymbolIcon 
                { 
                    Symbol = FluentAvalonia.UI.Controls.Symbol.Folder, 
                    Width = 16, 
                    Height = 16 
                });
                buttonContent.Children.Add(new TextBlock 
                { 
                    Text = "lblOpenLogsFolder".Localized(),
                    FontWeight = FontWeight.Bold
                });

                var openLogsButton = new Button
                {
                    Content = buttonContent,
                    Background = DesignTheme.Brush("Accent"),
                    Foreground = DesignTheme.Brush("OnAccent"),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    CornerRadius = new CornerRadius(6),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 15, 0, 15),
                    Padding = new Thickness(10, 6)
                };

                openLogsButton.Click += (_, _) =>
                {
                    try
                    {
                        var logFolder = Apps2Samsung.Diagnostics.FileLog.DefaultLogDirectory;
                        Directory.CreateDirectory(logFolder);

                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{logFolder}\"", UseShellExecute = true });
                        }
                        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                        {
                            Process.Start(new ProcessStartInfo { FileName = "open", Arguments = $"\"{logFolder}\"", UseShellExecute = false });
                        }
                        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                        {
                            Process.Start(new ProcessStartInfo { FileName = "xdg-open", Arguments = $"\"{logFolder}\"", UseShellExecute = false });
                        }
                        else
                        {
                            Process.Start(new ProcessStartInfo { FileName = logFolder, UseShellExecute = true });
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Failed to open Logs folder: {ex}");
                    }
                };

                stackPanel.Children.Add(openLogsButton);
                
                stackPanel.Children.Add(new TextBlock
                {
                    Text = logMessage,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Red,
                    FontSize = 14,
                    Margin = new Thickness(0, 5, 0, 0)
                });

                contentControl = stackPanel;
            }
            else
            {
                contentControl = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Red,
                    FontSize = 14,
                    Margin = new Thickness(0, 5, 0, 0)
                };
            }

            var dialog = CreateStyledDialog("Error", contentControl);

            if (window != null)
                await dialog.ShowDialog(window);
        }

        // The defaults have to be compile-time constants, so they are empty here and filled in
        // from the catalog below — otherwise an unlocalized "Yes"/"No" would ship.
        public async Task<bool> ShowConfirmationAsync(string title, string message, string? yesText = null, string? noText = null, Window? owner = null)
        {
            yesText ??= "keyYes".Localized();
            noText ??= "keyNo".Localized();
            var window = owner ?? GetMainWindow();
            var tcs = new TaskCompletionSource<bool>();
            var isDarkMode = AppSettings.Default.DarkMode;
            var foregroundBrush = GetThemeBrush("SystemControlForegroundBaseHighBrush", isDarkMode);

            var dialog = CreateStyledDialog(title, new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = foregroundBrush,
                FontSize = 14,
                Margin = new Thickness(0, 5, 0, 0)
            }, showButtons: true, tcs: tcs, yesText: yesText, noText: noText);

            if (window != null)
                await dialog.ShowDialog(window);

            return await tcs.Task;
        }

        public Task<bool> ShowCertificateCountdownAsync(DateTime validFromLocal) =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var window = GetMainWindow();
                if (window == null)
                    return false;
                return await new CertificateWaitWindow(validFromLocal, Localize).ShowDialog<bool>(window);
            });

        public Task<CertificateSheet?> OpenCertificateSheetAsync(string tvName, CertificatePrivilegeLevel level) =>
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                var window = GetMainWindow();
                if (window == null)
                    return (CertificateSheet?)null;
                var sheet = new CertificateWindow(tvName, level, Localize);
                // Not awaited: the sheet stays up while the certificate is made and closes itself.
                _ = sheet.ShowDialog(window);
                return new CertificateSheet(sheet);
            }).GetTask();

        public Task ShowAccountEmailMissingAsync() =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var window = GetMainWindow();
                if (window != null)
                    await new AccountEmailWindow(Localize).ShowDialog(window);
            });

        public Task<bool> ShowDevModeGuideAsync(
            NetworkDevice device,
            string tvName,
            Func<Task> refresh,
            IReadOnlyCollection<string> localIps,
            string? localIp,
            bool fromInstall) =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var window = GetMainWindow();
                if (window == null)
                    return false;
                var model = new DevModeGuideViewModel(device, tvName, refresh, localIps, localIp, fromInstall, Localize,
                    async text =>
                    {
                        if (window.Clipboard is { } clipboard)
                            await clipboard.SetTextAsync(text);
                    });
                return await new DevModeGuideWindow(model).ShowDialog<bool>(window);
            });

        public Task ShowLogViewerAsync() =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var window = GetMainWindow();
                if (window == null)
                    return;
                var model = new LogViewerViewModel(Apps2Samsung.Diagnostics.FileLog.DefaultLogDirectory, Localize, DesignTheme.Brush);
                // Not modal: it is a reading window, useful next to the app while reproducing a problem.
                new LogViewerWindow(model).Show(window);
                await Task.CompletedTask;
            });

        public Task<IReadOnlyDictionary<string, string>?> ShowUserSettingsAsync(
            string appName, UserSettingsSpec spec, IReadOnlyDictionary<string, string> current) =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var window = GetMainWindow();
                if (window == null)
                    return (IReadOnlyDictionary<string, string>?)null;
                return await new UserSettingsWindow(appName, spec, current, Localize).ShowDialog<IReadOnlyDictionary<string, string>?>(window);
            });

        private static string Localize(string key) => key.Localized();

        public async Task ShowMessageWithLinkAsync(string title, string message, string linkText, string url, string? closeText = null)
        {
            var window = GetMainWindow();
            if (window == null)
                return;

            var isDarkMode = AppSettings.Default.DarkMode;
            var foregroundBrush = GetThemeBrush("SystemControlForegroundBaseHighBrush", isDarkMode);

            var tcs = new TaskCompletionSource<bool>();
            var dialog = CreateStyledDialog(
                title,
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = foregroundBrush,
                    FontSize = 14,
                    Margin = new Thickness(0, 5, 0, 0)
                },
                showButtons: true,
                tcs: tcs,
                yesText: linkText,
                noText: closeText ?? "btn_Close".Localized(),
                // The link label is a sentence, not a "Yes" — let the button grow to fit it.
                onButtonsCreated: (yes, no) =>
                {
                    yes.Width = double.NaN;
                    yes.MinWidth = 90;
                    yes.Padding = new Thickness(14, 0);
                    no.Width = double.NaN;
                    no.MinWidth = 90;
                    no.Padding = new Thickness(14, 0);
                });

            // Closing via the window chrome never resolves the TCS on its own.
            dialog.Closed += (_, _) => tcs.TrySetResult(false);

            await dialog.ShowDialog(window);

            if (await tcs.Task)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Failed to open URL '{url}': {ex}");
                }
            }
        }

        public async Task<string?> PromptForIpAsync()
        {
            var window = GetMainWindow();
            var dialog = new IpInputDialog();

            if (window != null)
                return await dialog.ShowDialog<string?>(window);

            return null;
        }

        public async Task<string?> PromptForTextAsync(string title, string message, string placeholder)
        {
            var window = GetMainWindow();
            if (window == null)
                return null;

            var isDarkMode = AppSettings.Default.DarkMode;
            var foregroundBrush = GetThemeBrush("SystemControlForegroundBaseHighBrush", isDarkMode);

            var textBox = new TextBox
            {
                Watermark = placeholder,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
                FontSize = 14
            };

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = foregroundBrush,
                FontSize = 14,
                Margin = new Thickness(0, 5, 0, 0)
            });
            panel.Children.Add(textBox);

            // Reuse the app's styled dialog chrome (same Fluent look as the confirmation/IP prompts),
            // wiring OK/Cancel through a bool TCS and returning the entered text only on OK.
            var tcs = new TaskCompletionSource<bool>();
            var dialog = CreateStyledDialog(title, panel, showButtons: true, tcs: tcs, yesText: "Remove", noText: "Cancel");

            await dialog.ShowDialog(window);

            return await tcs.Task ? textBox.Text : null;
        }
    }
}
