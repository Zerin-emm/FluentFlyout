// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace FluentFlyoutWPF.Pages;

public partial class TaskbarVisualizerPage : Page
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public TaskbarVisualizerPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
    }

    // same as SystemPage.StartupHyperlink_RequestNavigate
    private void StartupHyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        // ms-settings: can fail (permission, group policy, a session without the Settings app); an escaping
        // exception here would terminate the process from a hyperlink click.
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to open {e.Uri.AbsoluteUri}");
        }
        finally
        {
            e.Handled = true;
        }
    }
}