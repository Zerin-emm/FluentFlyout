// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes.Utils
{
    /// <summary>
    /// Validates URLs that came from outside the process (API responses, toast arguments) before
    /// they are handed to the shell. Process.Start with UseShellExecute accepts far more than
    /// http(s): file://, UNC paths and ms-*: handlers would all be executed, so the input has to be
    /// allow-listed instead of trusted.
    /// </summary>
    internal static class UrlHelper
    {
        // every link this project hands to the shell points at the project repository; a tampered
        // release feed or a stale toast argument should not be able to open an arbitrary third-party page
        private static readonly string[] _allowedHostSuffixes = ["github.com"];

        /// <summary>
        /// Returns true when <paramref name="url"/> is an absolute https/http URL on a trusted host.
        /// </summary>
        public static bool IsSafeToOpen(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            {
                return false;
            }

            return IsAllowedHost(uri.Host);
        }

        private static bool IsAllowedHost(string host)
        {
            foreach (var suffix in _allowedHostSuffixes)
            {
                if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}