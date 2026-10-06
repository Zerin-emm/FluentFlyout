// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyoutWPF.Classes.Utils;
using Microsoft.Toolkit.Uwp.Notifications;
using NLog;
using System.Runtime.InteropServices;
using System.Windows;

namespace FluentFlyout.Classes;

[ComVisible(true)]
[Guid("79086E7F-0D65-4507-82B6-85F2288930D5")]
internal static class Notifications
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Handle toast notification activation
    /// </summary>
    public static void HandleNotificationActivation(ToastNotificationActivatedEventArgsCompat toastArgs)
    {
        try
        {
            // Obtain the arguments from the notification
            ToastArguments args = ToastArguments.Parse(toastArgs.Argument);

            // Check if the user clicked the notification
            if (args.TryGetValue("action", out string action))
            {
                switch (action)
                {
                    case "viewChanges":
                        OpenReleaseNotesInBrowser();
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to handle notification activation");
        }
    }

    /// <summary>
    /// Opens the release page of this repository, which lists what changed in the newest and the
    /// previously installed version.
    /// </summary>
    public static void OpenReleaseNotesInBrowser()
    {
        OpenUrlInBrowser(AppLinks.LatestRelease);
    }

    /// <summary>
    /// Resolves a localized resource to a string, tolerating a missing key.
    /// FindResource returns object, so both the lookup and ToString() can produce null.
    /// </summary>
    private static string ResolveString(string key)
    {
        var value = Application.Current?.TryFindResource(key);
        return value?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Show a Windows notification if the application is run for the first time or has been updated.
    /// </summary>
    /// <param name="lastKnownVersion"></param>
    /// <param name="currentVersion"></param>
    public static void ShowFirstOrUpdateNotification(string lastKnownVersion, string currentVersion)
    {
        if (string.IsNullOrEmpty(lastKnownVersion))
        {
            return;
        }

        if (lastKnownVersion != currentVersion)
        {
            try
            {
                // updated app version
                new ToastContentBuilder()
                    .AddText(ResolveString("UpdateToastTitle"))
                    .AddText(string.Format(ResolveString("UpdateToastMessage"), currentVersion))
                    .AddArgument("action", "viewChanges")
                    .AddButton(new ToastButton()
                        .SetContent(ResolveString("UpdateToastButton"))
                        .AddArgument("action", "viewChanges")
                        .SetBackgroundActivation())
                    .Show();

                Logger.Info($"Displayed update notification for {currentVersion}.");

                return;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to show update notification");
                return;
            }
        }
    }

    public static void OpenUrlInBrowser(string url)
    {
        if (string.IsNullOrEmpty(url)) return;

        if (!UrlHelper.IsSafeToOpen(url))
        {
            Logger.Warn($"Refusing to open URL with an untrusted scheme or host: {url}");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open URL in browser");
        }
    }
}