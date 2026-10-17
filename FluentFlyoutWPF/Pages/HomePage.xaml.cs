// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Services;
using FluentFlyoutWPF.Classes.Utils;
using FluentFlyoutWPF.ViewModels;
using NLog;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;

namespace FluentFlyoutWPF.Pages;

public partial class HomePage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public HomePage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;

        VersionTextBlock.Text = AppVersion.CurrentTag;

        UpdateLastCheckedText();
    }

    private void UpdateLastCheckedText()
    {
        if (UpdateState.Current.LastUpdateCheck != default)
        {
            LastCheckedText.Text = string.Format(
                Application.Current?.TryFindResource("LastChecked")?.ToString() ?? string.Empty,
                UpdateState.Current.LastCheckedText);
        }
        else
        {
            LastCheckedText.Text = string.Empty;
        }
    }

    private void ViewUpdates_Click(object sender, RoutedEventArgs e)
    {
        Notifications.OpenReleaseNotesInBrowser();
    }

    private long _lastChecked = 0;

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        // prevent multiple clicks within 1 second
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _lastChecked < 1)
        {
            return;
        }

        _lastChecked = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var result = await CheckForUpdatesAsync();

        if (result is not { Success: true, IsUpdateAvailable: true })
        {
            return;
        }

        // The update is a manual download, so the only action offered is to open the release page.
        // Declining it closes the dialog and nothing else happens - no reminder is scheduled.
        try
        {
            if (await UpdateCheckerService.ShowUpdateAvailableDialogAsync(result.NewestVersion))
            {
                UpdateCheckerService.OpenUpdateUrl(result.UpdateUrl);
            }
        }
        catch (Exception ex)
        {
            // An async void handler must not let an exception escape: it would reach
            // AppDomain.UnhandledException and take the whole application down.
            Logger.Error(ex, "Failed to show the update dialog");
        }
    }

    private async Task<UpdateCheckerService.UpdateCheckResult?> CheckForUpdatesAsync()
    {
        try
        {
            UpdateStatusText.Text = Application.Current.FindResource("CheckingForUpdates")?.ToString();

            var result = await UpdateCheckerService.CheckForUpdatesAsync(AppVersion.Current);

            if (result.Success)
            {
                UpdateState.Current.IsUpdateAvailable = result.IsUpdateAvailable;
                UpdateState.Current.NewestVersion = result.NewestVersion;
                UpdateState.Current.UpdateUrl = result.UpdateUrl;
                UpdateState.Current.LastUpdateCheck = result.CheckedAt;

                UpdateLastCheckedText();

                UpdateStatusText.Text = Application.Current.FindResource(
                    result.IsUpdateAvailable ? "UpdateAvailableNotificationTitle" : "UpToDate")?.ToString();
            }
            else
            {
                UpdateStatusText.Text = Application.Current.FindResource("UpToDate")?.ToString();
            }

            return result;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check for updates from HomePage");
            UpdateStatusText.Text = "Unable to check for updates"; // not localized
            return null;
        }
    }

    private void MediaFlyout_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(MediaFlyoutPage));
    }

    private void VolumeFlyout_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(VolumeMixerPage));
    }

    private void TaskbarWidget_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(TaskbarWidgetPage));
    }

    private void NextUp_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(NextUpPage));
    }

    private void LockKeys_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(LockKeysPage));
    }

    private void TaskbarVisualizer_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(TaskbarVisualizerPage));
    }

    private void System_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(SystemPage));
    }

    private void ViewLogs_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            Process.Start("explorer.exe", FileSystemHelper.GetLogsPath());
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open logs folder");
        }
    }

    private void ReportBug_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppLinks.ReportIssue,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open bug report page");
        }
    }
}