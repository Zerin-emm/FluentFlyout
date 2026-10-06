// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;

namespace FluentFlyoutWPF.Classes.Utils;

/// <summary>
/// The single source of truth for the running application version.
/// </summary>
/// <remarks>
/// The value comes from the assembly version, which the SDK derives from the &lt;Version&gt; property in
/// FluentFlyout.csproj. That is deliberately the same property a release has to bump, so the version
/// shown in the settings window and the version compared against the newest GitHub release can never
/// drift apart. The previous implementation read Package.Current, which only exists inside an MSIX
/// package and made every unpackaged build report itself as "debug".
/// </remarks>
internal static class AppVersion
{
    /// <summary>
    /// The current version without a leading "v", for example "2.2.0".
    /// </summary>
    public static string Current { get; } = Read();

    /// <summary>
    /// The current version in the shape release tags use, for example "v2.2.0".
    /// </summary>
    public static string CurrentTag => "v" + Current;

    private static string Read()
    {
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;

        if (version == null)
        {
            return "0.0.0";
        }

        // Build is -1 when only Major.Minor were specified; the UI and the release tags both use three
        // parts, so the missing ones are reported as 0 instead of leaking "-1" into the version string.
        return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }
}