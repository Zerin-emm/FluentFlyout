// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Utils;
using Microsoft.Win32;
using NLog;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;

namespace FluentFlyoutWPF.Pages;

public partial class SystemPage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const string StartupRunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRunValueName = "FluentFlyout";

    public SystemPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        UpdateMonitorList();

        // The switch is bound to the stored preference, which is not necessarily what Windows does.
        SyncStartupState();
    }

    private void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        ApplyStartupSwitch();
    }

    private void ApplyStartupSwitch()
    {
        bool enable = StartupSwitch.IsChecked ?? false;
        if (!SetStartup(enable))
        {
            // Windows refused the change, so show the state the system actually applies.
            SyncStartupState();
        }
    }

    /// <summary>
    /// Applies the requested startup state by writing the per-user Run value.
    /// </summary>
    /// <returns>True when the requested state was applied</returns>
    /// <remarks>
    /// This build is unpackaged and has no package identity, so there is no startup task to use: the
    /// HKCU Run value is the only mechanism. Writing it next to a packaged build's startup task is how
    /// the application used to be able to start twice, which is why the packaged branch is gone.
    /// </remarks>
    private static bool SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRunKeyPath, true);
            if (key == null)
            {
                Logger.Warn("The startup registry key is not accessible");
                return false;
            }

            if (enable)
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
                {
                    throw new FileNotFoundException("Application executable not found");
                }

                key.SetValue(StartupRunValueName, executablePath);
            }
            else if (key.GetValue(StartupRunValueName) != null)
            {
                key.DeleteValue(StartupRunValueName, false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to set the startup registry value");
            ShowStartupError(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Makes the stored preference match what Windows actually does
    /// </summary>
    /// <remarks>
    /// The toggle is bound to a stored setting, which can disagree with reality: startup can be turned
    /// off outside the application (Task Manager, Startup apps, group policy) and an older packaged
    /// build could have left a Run value pointing into WindowsApps.
    /// </remarks>
    private static void SyncStartupState()
    {
        try
        {
            RemoveLegacyPackagedRunEntry();

            bool? actual = GetStartupState();
            if (actual.HasValue && SettingsManager.Current.Startup != actual.Value)
            {
                SettingsManager.Current.Startup = actual.Value;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to read the startup state");
        }
    }

    private static bool? GetStartupState()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRunKeyPath, false);
        return key?.GetValue(StartupRunValueName) != null;
    }

    /// <summary>
    /// Deletes a leftover Run entry that points into the packaged application folder
    /// </summary>
    /// <remarks>
    /// Only entries inside WindowsApps are removed, so a portable copy that a user also wants to start
    /// is left untouched.
    /// </remarks>
    private static void RemoveLegacyPackagedRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRunKeyPath, true);
            if (key?.GetValue(StartupRunValueName) is not string runValue ||
                !runValue.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            key.DeleteValue(StartupRunValueName, false);
            Logger.Info("Removed the legacy Run entry that pointed into WindowsApps");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to remove the legacy startup Run entry");
        }
    }

    private static void ShowStartupError(string message)
    {
        MessageBox messageBox = new()
        {
            Title = "Error",
            Content = $"Failed to set startup: {message}",
            CloseButtonText = "OK",
        };

        _ = messageBox.ShowDialogAsync();
    }

    private void StartupHyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void ToggleSwitch_Click(object sender, RoutedEventArgs e)
    {
        // IsChecked is bool?, so an indeterminate switch would throw InvalidCastException here
        bool isChecked = NIconHideSwitch.IsChecked == true;

        if (Application.Current?.MainWindow is not MainWindow mainWindow)
        {
            Logger.Warn("The main window is not available, the tray icon was not changed.");
            return;
        }

        if (!isChecked)
        {
            mainWindow.nIcon.Register();
        }
        else
        {
            mainWindow.nIcon.Unregister();
        }
    }

    private void UpdateMonitorList()
    {
        MonitorUtil.UpdateMonitorList(
            FlyoutSelectedMonitorComboBox,
            () => SettingsManager.Current.FlyoutSelectedMonitor,
            value => SettingsManager.Current.FlyoutSelectedMonitor = value);
    }


    private async void ExportButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"FluentFlyout_Settings_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}",
            DefaultExt = ".xml",
            Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                SettingsManager.SaveSettings(saveFileDialog.FileName);

                Wpf.Ui.Controls.MessageBox messageBox = new()
                {
                    Title = Application.Current.FindResource("ExportSuccessful").ToString(),
                    Content = Application.Current.FindResource("SettingsExportedSuccessfully").ToString(),
                    CloseButtonText = "OK",
                };

                _ = messageBox.ShowDialogAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error exporting settings");

                Wpf.Ui.Controls.MessageBox messageBox = new()
                {
                    Title = Application.Current.FindResource("ExportFailed").ToString(),
                    Content = Application.Current.FindResource("FailedToExportSettings").ToString(),
                    CloseButtonText = "OK",
                };

                _ = messageBox.ShowDialogAsync();
            }
        }
    }

    private async void ImportButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            DefaultExt = ".xml",
            Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            Wpf.Ui.Controls.MessageBox confirmBox = new()
            {
                Title = Application.Current.FindResource("ImportSettings").ToString(),
                Content = Application.Current.FindResource("ImportSettingsWarning").ToString(),
                CloseButtonText = "No",
                SecondaryButtonText = "Yes",
            };

            var result = await confirmBox.ShowDialogAsync();

            if (result == Wpf.Ui.Controls.MessageBoxResult.Secondary)
            {
                try
                {
                    SettingsManager.RestoreSettings(openFileDialog.FileName);
                    SettingsManager.SaveSettings();

                    Wpf.Ui.Controls.MessageBox messageBox = new()
                    {
                        Title = Application.Current.FindResource("ImportSuccessful").ToString(),
                        Content = Application.Current.FindResource("SettingsImportedSuccessfully").ToString(),
                        CloseButtonText = "OK",
                    };

                    _ = messageBox.ShowDialogAsync();

                    // MainModule can be null (or throw) once the process starts shutting down, so the
                    // path has to be captured before that, and it should never be dereferenced blindly
                    string? executablePath = Environment.ProcessPath;

                    // Restart the application
                    Application.Current.Shutdown();

                    if (!string.IsNullOrEmpty(executablePath))
                    {
                        Process.Start(executablePath);
                    }
                    else
                    {
                        Logger.Warn("Could not determine the executable path, restart skipped.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error importing settings");

                    Wpf.Ui.Controls.MessageBox messageBox = new()
                    {
                        Title = Application.Current.FindResource("ImportFailed").ToString(),
                        Content = Application.Current.FindResource("FailedToImportSettings").ToString(),
                        CloseButtonText = "OK",
                    };

                    _ = messageBox.ShowDialogAsync();
                }
            }
        }
    }

    private void AppFiltering_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(AppFilteringPage));
    }

    private void Advanced_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(AdvancedPage));
    }
}