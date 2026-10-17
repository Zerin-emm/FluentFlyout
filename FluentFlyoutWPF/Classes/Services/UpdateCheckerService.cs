// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyoutWPF.Classes.Utils;
using NLog;
using System.IO;
using System.Net.Http;
using System.Xml;
using System.Xml.Linq;

namespace FluentFlyoutWPF.Classes.Services;

/// <summary>
/// Handles checking for application updates against the newest release of the project's repository.
/// </summary>
public static class UpdateCheckerService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // The Atom feed is used instead of the REST API on purpose: it needs no token and has no rate
    // limit, and a repository that has not published a release yet serves a valid empty feed instead
    // of a 404 - so "no release" is not an error and simply means there is nothing newer.
    private static readonly HttpClient _client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("FluentFlyout/" + AppVersion.Current);

        return client;
    }

    /// <summary>
    /// Result of an update check
    /// </summary>
    public class UpdateCheckResult
    {
        public bool IsUpdateAvailable { get; set; }
        public string NewestVersion { get; set; } = string.Empty;
        public string UpdateUrl { get; set; } = string.Empty;
        public DateTime CheckedAt { get; set; }
        public bool Success { get; set; }
    }

    /// <summary>
    /// Checks the repository's release feed for a version newer than <paramref name="currentVersion"/>.
    /// </summary>
    /// <param name="currentVersion">The current app version without a leading "v", for example "2.2.0"</param>
    /// <returns>UpdateCheckResult with update information</returns>
    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion)
    {
        var result = new UpdateCheckResult
        {
            CheckedAt = DateTime.Now,
            UpdateUrl = AppLinks.LatestRelease
        };

        try
        {
            using var feed = await _client.GetStreamAsync(AppLinks.LatestReleaseFeed);
            result.NewestVersion = await ReadNewestReleaseAsync(feed);
            result.Success = true;

            result.IsUpdateAvailable = IsNewerVersion(currentVersion, result.NewestVersion);

            Logger.Info($"Update check complete. Current: {currentVersion}, Newest: {result.NewestVersion}, Update available: {result.IsUpdateAvailable}");
        }
        catch (HttpRequestException ex)
        {
            Logger.Info($"Failed to check for updates - network error: {ex.Message}");
            result.Success = false;
        }
        catch (TaskCanceledException ex)
        {
            Logger.Info(ex, "Failed to check for updates - request timed out.");
            result.Success = false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unexpected error checking for updates");
            result.Success = false;
        }

        return result;
    }

    /// <summary>
    /// Reads the first entry of a GitHub releases Atom feed and returns its version.
    /// </summary>
    /// <param name="feed">The Atom feed stream</param>
    /// <returns>The version without a leading "v", or an empty string when the feed has no entry</returns>
    private static async Task<string> ReadNewestReleaseAsync(Stream feed)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };

        using var reader = XmlReader.Create(feed, settings);

        // The feed is newest-first, so the first entry is the newest release. It is read as a whole
        // (an entry carries the full release notes) and then abandoned, instead of parsing every
        // entry of the feed.
        while (await reader.ReadAsync())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "entry")
            {
                continue;
            }

            XNamespace atom = "http://www.w3.org/2005/Atom";
            var entry = (XElement)await XNode.ReadFromAsync(reader, CancellationToken.None);

            var link = entry.Elements(atom + "link")
                .FirstOrDefault(element => (string?)element.Attribute("rel") == "alternate");

            return ExtractVersion((string?)link?.Attribute("href"));
        }

        Logger.Info("Release feed contains no entry yet - no release has been published");

        return string.Empty;
    }

    /// <summary>
    /// Extracts the version from a release link, for example "2.2.0" from
    /// "https://github.com/owner/repo/releases/tag/v2.2.0".
    /// </summary>
    private static string ExtractVersion(string? releaseUrl)
    {
        if (string.IsNullOrWhiteSpace(releaseUrl))
        {
            return string.Empty;
        }

        // The title is not used for this: it holds the release notes headline ("v2.2.0 - Something
        // New"), which cannot be parsed as a version.
        const string marker = "/releases/tag/";

        int markerIndex = releaseUrl.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        string tag = releaseUrl[(markerIndex + marker.Length)..];

        return Uri.UnescapeDataString(tag).Trim().TrimStart('v', 'V');
    }

    /// <summary>
    /// Asks the user whether to open the download page.
    /// </summary>
    /// <param name="newestVersion">The newer version, shown inside the message</param>
    /// <returns>True when the user chose to visit the release page</returns>
    public static async Task<bool> ShowUpdateAvailableDialogAsync(string newestVersion)
    {
        try
        {
            Wpf.Ui.Controls.MessageBox messageBox = new()
            {
                Title = ResolveString("UpdateAvailableDialogTitle"),
                Content = string.Format(ResolveString("UpdateAvailableDialogMessage"), newestVersion),
                PrimaryButtonText = ResolveString("UpdateDialogVisitButton"),
                CloseButtonText = ResolveString("UpdateDialogLaterButton")
            };

            var result = await messageBox.ShowDialogAsync();

            return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to show the update dialog");
            return false;
        }
    }

    private static string ResolveString(string key)
    {
        var value = System.Windows.Application.Current?.TryFindResource(key);
        return value?.ToString() ?? string.Empty;
    }

    public static void OpenUpdateUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;

        // The URL comes from an Atom feed, so it is treated as untrusted input instead of being
        // handed to the shell directly
        if (!UrlHelper.IsSafeToOpen(url))
        {
            Logger.Warn($"Refusing to open update URL with an untrusted scheme or host: {url}");
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
            Logger.Error(ex, "Failed to open update URL");
        }
    }

    private static bool IsNewerVersion(string currentVersion, string newestVersion)
    {
        try
        {
            // Unparseable values (for example a pre-release tag such as "2.3.0-beta.1") are not an
            // update - reporting an unknown version as newer would nag about something that is not
            // necessarily newer at all
            if (string.IsNullOrEmpty(newestVersion))
            {
                return false;
            }

            var current = Version.Parse(currentVersion.TrimStart('v'));
            var newest = Version.Parse(newestVersion.TrimStart('v'));
            return newest > current;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to compare versions: {currentVersion} vs {newestVersion}");
            return false;
        }
    }
}