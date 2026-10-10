// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static FluentFlyout.Classes.NativeMethods;
namespace FluentFlyout.Classes.Utils;

public static class MediaPlayerData
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private class CachedMediaPlayerInfo
    {
        public required string Title { get; set; }
        public ImageSource? Icon { get; set; }
        public int ProcessId { get; set; }
        public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;
    }
    // cache for media player info to avoid redundant process lookups
    private static readonly ConcurrentDictionary<string, CachedMediaPlayerInfo> mediaPlayerCache = [];

    // id variants of media players where the key is the mediaPlayerId and the value is the mediaPlayerCache key
    private static readonly ConcurrentDictionary<string, string> mediaPlayerIdVariants = [];

    private static Process[]? cachedProcesses = null;
    private static DateTime lastCacheTime = DateTime.MinValue;
    private const int CACHE_DURATION_SECONDS = 5;

    /// <summary>
    /// Upper bound for the caches. Entries are keyed by media player id or by window title, and window
    /// titles are arbitrary and change constantly, so an unbounded dictionary would keep growing for
    /// the whole lifetime of the process.
    /// </summary>
    private const int MaxCacheEntries = 64;

    /// <summary>
    /// Drops the least recently used entries when the cache grew past its limit.
    /// </summary>
    private static void PruneCache()
    {
        if (mediaPlayerCache.Count <= MaxCacheEntries && mediaPlayerIdVariants.Count <= MaxCacheEntries)
            return;

        var expired = mediaPlayerCache
            .OrderByDescending(entry => entry.Value.LastAccessUtc)
            .Skip(MaxCacheEntries)
            .Select(entry => entry.Key)
            .ToList();

        foreach (string key in expired)
        {
            mediaPlayerCache.TryRemove(key, out _);
        }

        if (mediaPlayerIdVariants.Count > MaxCacheEntries)
        {
            // the variant map is worthless without its target entry, so drop whatever no longer resolves
            foreach (string key in mediaPlayerIdVariants.Keys.ToList())
            {
                if (!mediaPlayerIdVariants.TryGetValue(key, out string? target) || !mediaPlayerCache.ContainsKey(target))
                {
                    mediaPlayerIdVariants.TryRemove(key, out _);
                }
            }
        }
    }

    public static (string, ImageSource?) GetAndCacheMediaPlayerData(string mediaPlayerId)
    {
        PruneCache();

        if (mediaPlayerCache.TryGetValue(mediaPlayerId, out var cachedInfo)
            || mediaPlayerIdVariants.TryGetValue(mediaPlayerId, out var variantKey)
            && mediaPlayerCache.TryGetValue(variantKey, out cachedInfo))
        {
            cachedInfo.LastAccessUtc = DateTime.UtcNow;
            return (cachedInfo.Title, cachedInfo.Icon);
        }

        string mediaTitle = mediaPlayerId;
        ImageSource? mediaIcon = null;

        // get sanitized media title name
        string[] mediaSessionIdVariants = mediaPlayerId.Split('.');

        // remove common non-informative substrings
        var variants = mediaSessionIdVariants.Select(variant =>
            variant.Replace("com", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("github", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("exe", "", StringComparison.OrdinalIgnoreCase)
                   .Trim()
        ).Where(variant => !string.IsNullOrWhiteSpace(variant)).ToList();

        // add original id to the end of the array to ensure at least one variant
        variants.Add(mediaPlayerId);
        if (variants.Any(v => v.Contains("MicrosoftEdge", StringComparison.OrdinalIgnoreCase)
            || v.Equals("MSEdge", StringComparison.OrdinalIgnoreCase)
            || v.EndsWith("!MSEdge", StringComparison.OrdinalIgnoreCase))) variants.Add("msedge");

        Process[] processes;

        // use cache to avoid frequent process enumeration
        if (cachedProcesses == null || (DateTime.Now - lastCacheTime).TotalSeconds > CACHE_DURATION_SECONDS)
        {
            // Process objects hold an open OS handle as soon as one of their properties is read, and
            // they only release it on Dispose (the finalizer is a fallback, not a plan). Replacing the
            // array without disposing the previous one leaked a handle per running process every few
            // seconds for the whole lifetime of the app.
            DisposeCachedProcesses();

            cachedProcesses = Process.GetProcesses();
            lastCacheTime = DateTime.Now;
        }

        processes = cachedProcesses;

        var processData = processes.Select(p =>
            {
                try
                {
                    // pre-filter processes without a main window handle unless they are an exact match to the media player id
                    bool isExactMatch = variants.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase);
                    if (!isExactMatch && p.MainWindowHandle == IntPtr.Zero)
                    {
                        return null;
                    }

                    var mainModule = p.MainModule;
                    if (mainModule == null) return null;

                    string path = mainModule.FileName;

                    if (isExactMatch || variants.Any(v => path.Contains(v, StringComparison.OrdinalIgnoreCase)))
                    {
                        // prioritize the FileDescription for a user-friendly name
                        // fall back to MainWindowTitle if the description is empty
                        string title = !string.IsNullOrWhiteSpace(mainModule.FileVersionInfo.FileDescription)
                                        ? mainModule.FileVersionInfo.FileDescription
                                        : p.MainWindowTitle;

                        return new { Title = title, Path = path, ProcessId = p.Id, IsExactMatch = isExactMatch };
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // silently ignore the exception for inaccessible processes
                }
                catch (InvalidOperationException)
                {
                    // process exited while being inspected
                }
                return null;
            })
            .OrderByDescending(data => data != null && data.IsExactMatch)
            .FirstOrDefault(data => data != null); // use first result

        if (processData == null)
        {
            var (shellTitle, shellIcon) = ResolveViaAppsFolder(mediaPlayerId);
            if (shellTitle == null) return (mediaTitle, mediaIcon);

            mediaPlayerCache[mediaPlayerId] = new CachedMediaPlayerInfo
            {
                Title = shellTitle,
                Icon = shellIcon,
                ProcessId = -1 // no process match, -1 avoids colliding with real PIDs
            };

            return (shellTitle, shellIcon);
        }

        mediaTitle = !string.IsNullOrWhiteSpace(processData.Title) ? processData.Title : mediaPlayerId;

        // check cache again because we have the sanitized title
        if (mediaPlayerCache.TryGetValue(mediaTitle, out cachedInfo))
        {
            // map the original id to the sanitized title for future lookups
            mediaPlayerIdVariants[mediaPlayerId] = mediaTitle;
            return (cachedInfo.Title, cachedInfo.Icon);
        }

        mediaIcon = GetIconFromPath(processData.Path);

        mediaPlayerCache[mediaPlayerId] = new CachedMediaPlayerInfo
        {
            Title = mediaTitle,
            Icon = mediaIcon,
            ProcessId = processData.ProcessId
        };

        return (mediaTitle, mediaIcon);
    }

    public static int? GetAndCacheProcessId(string mediaPlayerId)
    {
        GetAndCacheMediaPlayerData(mediaPlayerId);

        if (!mediaPlayerCache.TryGetValue(mediaPlayerId, out var cachedInfo)
            && (!mediaPlayerIdVariants.TryGetValue(mediaPlayerId, out var variantKey)
            || !mediaPlayerCache.TryGetValue(variantKey, out cachedInfo))) return null;

        return cachedInfo.ProcessId > 0 ? cachedInfo.ProcessId : null;
    }

    /// <summary>
    /// Looks the cached entry up, following the id-variant indirection when needed.
    /// </summary>
    private static bool TryGetCachedInfo(string mediaPlayerId, out CachedMediaPlayerInfo cachedInfo)
    {
        if (mediaPlayerCache.TryGetValue(mediaPlayerId, out cachedInfo!))
            return true;

        if (mediaPlayerIdVariants.TryGetValue(mediaPlayerId, out string? variantKey)
            && mediaPlayerCache.TryGetValue(variantKey, out cachedInfo!))
            return true;

        cachedInfo = null!;
        return false;
    }

    /// <summary>
    /// Drops everything the cache holds for one media player id, so the next lookup resolves it again.
    /// </summary>
    /// <remarks>
    /// The variant map has to go with the entry: it is keyed by the raw session id and points at the
    /// sanitized title, so leaving it behind would keep steering every later lookup at the removed entry.
    /// </remarks>
    private static void InvalidateCacheEntry(string mediaPlayerId)
    {
        foreach (var variant in mediaPlayerIdVariants.ToList())
        {
            if (string.Equals(variant.Key, mediaPlayerId, StringComparison.Ordinal)
                || string.Equals(variant.Value, mediaPlayerId, StringComparison.Ordinal))
            {
                mediaPlayerCache.TryRemove(variant.Key, out _);
                mediaPlayerCache.TryRemove(variant.Value, out _);
                mediaPlayerIdVariants.TryRemove(variant.Key, out _);
            }
        }

        mediaPlayerCache.TryRemove(mediaPlayerId, out _);
    }

    public static bool TryActivateMediaPlayer(string mediaPlayerId, string? mediaTitle = null)
    {
        GetAndCacheMediaPlayerData(mediaPlayerId);
        if (!TryGetCachedInfo(mediaPlayerId, out var cachedInfo)) return false;

        try
        {
            return ActivateProcess(cachedInfo.ProcessId, mediaTitle);
        }
        catch (ArgumentException)
        {
            Logger.Info("Cached media player process {0} is no longer running, resolving it again", cachedInfo.ProcessId);
            InvalidateCacheEntry(mediaPlayerId);

            GetAndCacheMediaPlayerData(mediaPlayerId);
            if (!TryGetCachedInfo(mediaPlayerId, out cachedInfo)) return false;

            try
            {
                return ActivateProcess(cachedInfo.ProcessId, mediaTitle);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to activate media player after re-resolving it");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to activate media player");
            return false;
        }
    }

    /// <summary>
    /// Brings an already resolved media player process to the foreground, or starts it again.
    /// </summary>
    private static bool ActivateProcess(int processId, string? mediaTitle)
    {
        using var process = Process.GetProcessById(processId);
        IntPtr handle = process.MainWindowHandle;
        if (IsBrowser(process.ProcessName)) return TryActivateBrowserTab(process.ProcessName, mediaTitle);

        if (handle == IntPtr.Zero)
        {
            foreach (var candidate in Process.GetProcessesByName(process.ProcessName))
            {
                handle = candidate.MainWindowHandle;
                candidate.Dispose();
                if (handle != IntPtr.Zero) break;
            }
        }

        if (handle != IntPtr.Zero)
        {
            if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
            return SetForegroundWindow(handle);
        }

        string? path = process.MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(path) && !IsBrowser(System.IO.Path.GetFileNameWithoutExtension(path)))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }

        return false;
    }

    private static bool TryActivateBrowserTab(string processName, string? mediaTitle)
    {
        if (!IsBrowser(processName) || string.IsNullOrWhiteSpace(mediaTitle)) return false;

        foreach (var browserProcess in Process.GetProcessesByName(processName))
        {
            try
            {
                var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ProcessIdProperty, browserProcess.Id),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)));

                foreach (AutomationElement window in windows)
                {
                    IntPtr handle = new(window.Current.NativeWindowHandle);
                    if (handle == IntPtr.Zero) continue;
                    if (IsIconic(handle))
                    {
                        ShowWindow(handle, SW_RESTORE);
                        Thread.Sleep(100);
                    }

                    var tabs = window.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

                    foreach (AutomationElement tab in tabs)
                    {
                        if (!tab.Current.Name.Contains(mediaTitle, StringComparison.OrdinalIgnoreCase)) continue;
                        if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
                            ((SelectionItemPattern)pattern).Select();
                        else if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                            ((InvokePattern)pattern).Invoke();
                        else continue;

                        if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
                        return SetForegroundWindow(handle);
                    }
                }
            }
            catch { }
            finally
            {
                browserProcess.Dispose();
            }
        }

        return false;
    }

    private static bool IsBrowser(string processName) =>
        new[] { "chrome", "msedge" }
        .Contains(processName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Disposes every <see cref="Process"/> in the process snapshot and clears the reference.
    /// </summary>
    private static void DisposeCachedProcesses()
    {
        Process[]? processes = cachedProcesses;
        cachedProcesses = null;

        if (processes == null) return;

        foreach (Process process in processes)
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Extracts the associated icon for a given process ID. Returns null if the process is inaccessible.
    /// </summary>
    public static ImageSource? GetAndCacheProcessIcon(int processId, string title)
    {
        try
        {
            if (title == "System sounds") return null;

            // search in cache
            foreach (var item in mediaPlayerCache.Values)
            {
                if (item.ProcessId == processId)
                {
                    return item.Icon;
                }
            }

            using var process = Process.GetProcessById(processId);
            var path = process.MainModule?.FileName;
            if (path == null) return null;

            // store in cache for future lookups
            var icon = GetIconFromPath(path);
            if (icon != null)
            {
                mediaPlayerCache[title] = new CachedMediaPlayerInfo
                {
                    Title = title,
                    Icon = icon,
                    ProcessId = processId
                };
            }

            return icon;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? GetIconFromPath(string exePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;

            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();

            return source;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves an AppUserModelId through the shell Apps folder ("shell:AppsFolder"),
    /// the same way the native media flyout does. Handles apps whose session id
    /// doesn't match their process path, e.g. Firefox-based browsers (#949).
    /// </summary>
    private static (string? Title, ImageSource? Icon) ResolveViaAppsFolder(string appUserModelId)
    {
        object? shell = null;
        object? folder = null;
        object? item = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return (null, null);

            shell = Activator.CreateInstance(shellType);
            if (shell is null) return (null, null);

            dynamic shellApp = shell;
            folder = shellApp.NameSpace("shell:AppsFolder");
            if (folder is null) return (null, null);

            dynamic appsFolder = folder;
            item = appsFolder.ParseName(appUserModelId);
            if (item is null) return (null, null);

            dynamic shellItem = item;
            string name = shellItem.Name;
            if (string.IsNullOrWhiteSpace(name)) return (null, null);

            // desktop apps expose their start menu shortcut target, so the icon can
            // be extracted the same way as everywhere else; packaged apps don't
            // have one and keep a null icon
            var targetPath = shellItem.ExtendedProperty("System.Link.TargetParsingPath") as string;
            ImageSource? icon = targetPath != null ? GetIconFromPath(targetPath) : null;

            return (name, icon);
        }
        catch
        {
            // id is not registered in the apps folder, nothing we can do
            return (null, null);
        }
        finally
        {
            // these are COM RCWs - the CLR does not release them deterministically, and a busy media
            // flyout resolves names often enough for the shells to pile up
            ReleaseComObject(item);
            ReleaseComObject(folder);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject == null) return;

        try
        {
            if (Marshal.IsComObject(comObject))
            {
                Marshal.FinalReleaseComObject(comObject);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to release a shell automation COM object");
        }
    }
}