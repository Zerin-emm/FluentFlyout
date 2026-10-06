// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;

namespace FluentFlyoutWPF.Classes.Utils
{
    internal class FileSystemHelper
    {
        /// <summary>
        /// Returns the folder NLog.config writes its log files to.
        /// </summary>
        /// <remarks>
        /// NLog.config targets <c>${specialfolder:folder=ApplicationData}/FluentFlyout</c>, which is
        /// always %AppData%\FluentFlyout for this build - it is never packaged as MSIX, so the
        /// package-redirected paths the Store build used do not exist here.
        /// </remarks>
        public static string GetLogsPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FluentFlyout");
        }
    }
}