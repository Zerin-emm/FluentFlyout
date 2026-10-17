// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF;
using MicaWPF.Core.Enums;
using MicaWPF.Core.Helpers;
using MicaWPF.Core.Services;
using System.Windows;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Tray.Controls;

namespace FluentFlyout.Classes;

/// <summary>
/// Manages the application theme settings and applies the selected theme.
/// </summary>
internal static class ThemeManager
{
    /// <summary>
    /// Whether the system theme watcher is currently installed for the main window.
    /// </summary>
    /// <remarks>
    /// Watched separately from the watcher itself so that watching and unwatching are idempotent: the
    /// WPF-UI watcher does not tolerate being subscribed twice for the same window, and the previous
    /// implementation returned early from <see cref="UnWatchThemeChanges"/> whenever the main window
    /// was not loaded yet, which left a subscription behind that was never removed again.
    /// </remarks>
    private static bool _isWatchingSystemTheme;

    /// <summary>
    /// Applies the theme saved in the application settings. Used at application startup.
    /// </summary>
    /// <inheritdoc cref="ApplyTheme"/>
    public static void ApplySavedTheme()
    {
        ApplyTheme(SettingsManager.Current.AppTheme);
        UpdateTrayIcon();
        UpdateTaskbarWidget();
    }

    /// <summary>
    /// Applies the theme saved in the application settings without touching the tray icon or the taskbar
    /// widget, neither of which exists yet where this runs.
    /// </summary>
    /// <remarks>
    /// The acrylic tint is a one-shot value: <see cref="WindowBlurHelper"/> bakes the current theme's
    /// background brush into a window's accent policy when that window is created and nothing repaints it
    /// afterwards. The first flyout after a cold start is created from the first media event, which the
    /// dispatcher delivers long before the main window reaches Loaded - so without this call that flyout
    /// keeps the light background brush and shows a light background in dark mode for its whole lifetime.
    /// </remarks>
    /// <inheritdoc cref="ApplyTheme"/>
    public static void ApplySavedThemeEarly() => ApplyTheme(SettingsManager.Current.AppTheme);

    /// <summary>
    /// Applies the specified theme and saves it to the application settings.
    /// </summary>
    /// <inheritdoc cref="ApplyTheme"/>
    public static void ApplyAndSaveTheme(int theme)
    {
        ApplyTheme(theme);
        SettingsManager.Current.AppTheme = theme;
        SettingsManager.SaveSettings();

        if (SettingsManager.Current.MediaFlyoutAcrylicWindowEnabled) { WindowBlurHelper.EnableBlur(Application.Current.MainWindow); }
        UpdateTaskbarWidget();
    }

    /// <summary>
    /// Applies the specified theme. See also <see href="https://github.com/Simnico99/MicaWPF/wiki/Change-Theme-or-Accent-color"/>.
    /// </summary>
    /// <param name="theme">The theme to apply. 1 for Light, 2 for Dark, 0 or any other value for System Default.</param>
    /// <remarks>
    /// Both theme systems have to be driven together: the settings window and the flyout controls are
    /// WPF-UI (<see cref="ApplicationThemeManager"/>) while <c>MicaWindow</c> and everything MicaWPF
    /// contributes bring their own <c>ThemeService</c>. Applying one and not the other leaves one half
    /// of the UI in the previous theme, so the two calls below are intentionally paired.
    /// </remarks>
    private static void ApplyTheme(int theme)
    {
        switch (theme)
        {
            case 1:
                UnWatchThemeChanges();
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                MicaWPFServiceUtility.ThemeService.ChangeTheme(WindowsTheme.Light);
                break;
            case 2:
                UnWatchThemeChanges();
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                MicaWPFServiceUtility.ThemeService.ChangeTheme(WindowsTheme.Dark);
                break;
            default:
                WatchThemeChanges();
                ApplicationThemeManager.ApplySystemTheme();
                MicaWPFServiceUtility.ThemeService.ChangeTheme(/*WindowsTheme.Auto*/);
                break;
        }

        // refresh accent color to its counterpart after theme changes
        MicaWPFServiceUtility.AccentColorService.RefreshAccentsColors();

        // A window's acrylic tint is baked in when the window is created, so every window that is already
        // open still carries the previous theme's background colour. Re-applying the tint here is what
        // repairs them - including a flyout that was created before this theme was ever applied.
        WindowBlurHelper.AdjustBlurOpacityForAllWindows(SettingsManager.Current.AcrylicBlurOpacity);
    }

    /// <summary>
    /// Starts watching for system theme changes and applies them automatically. (just a wrapper for <see cref="SystemThemeWatcher.Watch"/>)
    /// </summary>
    /// <remarks>This function was not necessary because the theme was managed by MicaWPF.</remarks>
    private static void WatchThemeChanges()
    {
        if (_isWatchingSystemTheme)
            return;

        // The watcher is installed for the main window, so there is nothing to watch before the window
        // exists. ApplySavedTheme runs again once the window is loaded, which is when this succeeds.
        if (Application.Current.MainWindow is not { IsLoaded: true } window)
            return;

        _isWatchingSystemTheme = true;
        SystemThemeWatcher.Watch(window);
    }

    /// <summary>
    /// Stops watching for system theme changes. (just a wrapper for <see cref="SystemThemeWatcher.UnWatch"/>)
    /// </summary>
    /// <remarks>This function was not necessary because the theme was managed by MicaWPF.</remarks>
    private static void UnWatchThemeChanges()
    {
        if (!_isWatchingSystemTheme)
            return;

        _isWatchingSystemTheme = false;

        if (Application.Current.MainWindow is { } window)
            SystemThemeWatcher.UnWatch(window);
    }

    /// <summary>
    /// Changes the tray icon according to the specified app theme and setting.
    /// </summary>
    public static void UpdateTrayIcon()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (Application.Current.MainWindow.FindName("nIcon") is NotifyIcon nIcon)
            {
                if (SettingsManager.Current.NIconSymbol == true)
                {
                    WindowsThemeDetector.GetWindowsTheme(out _, out var systemTheme);
                    var iconUri = new Uri(systemTheme == WindowsThemeDetector.ThemeMode.Dark
                        ? "pack://application:,,,/Resources/TrayIcons/FluentFlyoutWhite.png"
                        : "pack://application:,,,/Resources/TrayIcons/FluentFlyoutBlack.png");
                    nIcon.Icon = new BitmapImage(iconUri);
                }
                else
                {
                    var iconUi = new Uri("pack://application:,,,/Resources/FluentFlyout2.ico");
                    nIcon.Icon = new BitmapImage(iconUi);
                }
            }
        });
    }

    /// <summary>
    /// Updates the taskbar widget theme to match the current Windows theme.
    /// </summary>
    public static void UpdateTaskbarWidget()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (Application.Current.MainWindow is not MainWindow mainWindow)
                return;

            mainWindow.taskbarWindow?.Widget?.ApplyWindowsTheme();
        });
    }
}