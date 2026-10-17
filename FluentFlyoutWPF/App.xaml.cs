// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyoutWPF.Classes.Services;
using Microsoft.Toolkit.Uwp.Notifications;
using System.Windows;

namespace FluentFlyoutWPF;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // log unhandled exceptions before crashing
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            NLog.LogManager.GetCurrentClassLogger().Fatal(args.ExceptionObject as Exception, "Unhandled exception occurred");
            NLog.LogManager.Flush(); // Ensure logs are written before application dies
        };

        // Exceptions thrown on the dispatcher (UI) thread are unwound into the WPF message loop and never
        // reach the handler above; without this they terminate the process. The WPF-UI NavigationView can
        // throw a NullReferenceException from its VisualState transition while its ControlTemplate is being
        // replaced (which is what happens right after a theme change), and losing the whole application
        // because the settings pane failed to repaint is never the right outcome.
        DispatcherUnhandledException += (sender, args) =>
        {
            if (IsNavigationViewVisualStateFailure(args.Exception))
            {
                NLog.LogManager.GetCurrentClassLogger().Error(args.Exception,
                    "Recovered from a WPF-UI NavigationView visual state failure");
                args.Handled = true;
            }
        };

        // Register AUMID for toast notifications
        ToastNotificationManagerCompat.OnActivated += Notifications.HandleNotificationActivation;

        base.OnStartup(e);
    }

    /// <summary>
    /// The WPF-UI NavigationView throws a NullReferenceException from
    /// VisualStateManager.GenerateDynamicTransitionAnimations (StyleHelper.FindNameInTemplateContent)
    /// when it changes state while its ControlTemplate is stale - which is what the theme dictionaries
    /// being swapped leaves behind. Only that specific, known failure is swallowed.
    /// </summary>
    private static bool IsNavigationViewVisualStateFailure(Exception? exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is NullReferenceException &&
                current.StackTrace is string stackTrace &&
                stackTrace.Contains("Wpf.Ui.Controls.NavigationView") &&
                stackTrace.Contains("VisualStateManager"))
            {
                return true;
            }
        }

        return false;
    }
}