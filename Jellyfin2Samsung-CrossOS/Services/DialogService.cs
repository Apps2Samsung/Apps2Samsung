using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Apps2Samsung.Helpers;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Extensions;
using System;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
                return desktop.MainWindow;

            return null;
        }

        private Window CreateStyledDialog(
            string title,
            Control content,
            bool showButtons = false,
            TaskCompletionSource<bool>? tcs = null,
            string yesText = "Yes",
            string noText = "No")
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
                    Background = new SolidColorBrush(Color.Parse("#2563eb")),
                    Foreground = Brushes.White,
                    CornerRadius = new CornerRadius(8),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };

                var noButton = new Button
                {
                    Content = noText,
                    Width = 90,
                    Height = 35,
                    Background = new SolidColorBrush(Color.Parse("#9ca3af")),
                    Foreground = Brushes.White,
                    CornerRadius = new CornerRadius(8),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };

                yesButton.Click += (_, _) => { tcs.SetResult(true); dialog.Close(); };
                noButton.Click += (_, _) => { tcs.SetResult(false); dialog.Close(); };

                buttons.Children.Add(yesButton);
                buttons.Children.Add(noButton);

                mainPanel.Children.Add(buttons);
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
                    Background = new SolidColorBrush(Color.Parse("#2C3E50")),
                    Foreground = Brushes.White,
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
                        var logFolder = Path.Combine(AppContext.BaseDirectory, "Logs");
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

        public async Task<bool> ShowConfirmationAsync(string title, string message, string yesText = "Yes", string noText = "No", Window? owner = null)
        {
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

        public async Task<string?> PromptForIpAsync()
        {
            var window = GetMainWindow();
            var dialog = new IpInputDialog();

            if (window != null)
                return await dialog.ShowDialog<string?>(window);

            return null;
        }
    }
}
