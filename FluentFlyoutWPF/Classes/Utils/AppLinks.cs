// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes.Utils;

/// <summary>
/// The links the application points at. They live in one place so that the update check and every
/// "open in browser" button cannot drift apart.
/// </summary>
internal static class AppLinks
{
    /// <summary>
    /// The repository this build is published from.
    /// </summary>
    public const string Repository = "https://github.com/Zerin-emm/FluentFlyout";

    /// <summary>
    /// Where users report a bug.
    /// </summary>
    public const string ReportIssue = Repository + "/issues/new/choose";

    /// <summary>
    /// The release page shown after an update, and the place users download a new version from.
    /// GitHub redirects this to the newest release, or to the release list while none exists yet.
    /// </summary>
    public const string LatestRelease = Repository + "/releases/latest";

    /// <summary>
    /// The Atom feed of releases, used by the update check.
    /// </summary>
    public const string LatestReleaseFeed = Repository + "/releases.atom";
}