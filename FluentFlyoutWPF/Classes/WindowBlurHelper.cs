// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Appearance;
using static FluentFlyout.Classes.NativeMethods;

namespace FluentFlyout.Classes;

public static class WindowBlurHelper
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Tint used for the light theme when the theme brush cannot be resolved
    /// </summary>
    private const uint LightFallbackBackgroundColor = 0xF3F3F3;

    /// <summary>
    /// Tint used for the dark theme when the theme brush cannot be resolved
    /// </summary>
    private const uint DarkFallbackBackgroundColor = 0x202020;

    /// <summary>
    /// Enables acrylic blur effect on the specified window
    /// </summary>
    /// <param name="window">The window to apply blur to</param>
    public static void EnableBlur(Window window)
    {
        uint blurOpacity = Math.Clamp(SettingsManager.Current.AcrylicBlurOpacity, 0, 255);

        ApplyAccent(window, blurOpacity, GetBlurBackgroundColor());
    }

    /// <summary>
    /// Disables blur effect on the specified window
    /// </summary>
    /// <param name="window">The window to disable blur on</param>
    public static void DisableBlur(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            Logger.Debug("Skipping blur removal for {0} because it has no window handle yet", window.GetType().Name);
            return;
        }

        TrySetAccent(handle, AccentState.ACCENT_DISABLED, 0, 0);
    }

    /// <summary>
    /// Adjusts the blur opacity for all windows that have acrylic blur enabled
    /// </summary>
    /// <param name="newBlurOpacity">New opacity value (0-255)</param>
    public static void AdjustBlurOpacityForAllWindows(uint newBlurOpacity)
    {
        newBlurOpacity = Math.Clamp(newBlurOpacity, 0, 255);

        uint blurBackgroundColor = GetBlurBackgroundColor();

        foreach (Window window in Application.Current.Windows)
        {
            if (window == null) continue;

            // check if window should have acrylic blur based on settings
            if (ShouldHaveAcrylicBlur(window))
            {
                ApplyAccent(window, newBlurOpacity, blurBackgroundColor);
            }
        }
    }

    /// <summary>
    /// Applies the strongest accent policy the running OS supports to a window
    /// </summary>
    /// <remarks>
    /// Acrylic was added in Windows 10 1803 (build 17134); older builds only understand the plain
    /// accent blur. The effect is therefore chosen from the OS build instead of from the return value
    /// of <c>SetWindowCompositionAttribute</c>: that undocumented call also reports failure in cases
    /// where the effect was applied, so degrading on it would silently change the look of every system
    /// where acrylic works. <see cref="OperatingSystem.IsWindowsVersionAtLeast(int, int, int)"/> is
    /// used instead of <c>Environment.OSVersion</c> because the latter is filtered by the application
    /// manifest, and this package declares a MaxVersionTested of 10.0.0.0.
    /// </remarks>
    private static void ApplyAccent(Window window, uint blurOpacity, uint backgroundColor)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            Logger.Debug("Skipping accent policy for {0} because it has no window handle yet", window.GetType().Name);
            return;
        }

        bool acrylicAvailable = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134);
        if (!acrylicAvailable)
        {
            Logger.Info("Acrylic blur requires Windows 10 1803 or newer, using the plain blur accent instead");
        }

        AccentState accentState = acrylicAvailable
            ? AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND
            : AccentState.ACCENT_ENABLE_BLURBEHIND;

        if (!TrySetAccent(handle, accentState, blurOpacity, backgroundColor))
        {
            Logger.Warn("SetWindowCompositionAttribute failed for {0}", window.GetType().Name);
        }
    }

    /// <summary>
    /// Builds the accent policy for one effect and hands it to the compositor
    /// </summary>
    /// <returns><see langword="true"/> when the native call reported success</returns>
    private static bool TrySetAccent(IntPtr handle, AccentState accentState, uint blurOpacity, uint backgroundColor)
    {
        var accent = new AccentPolicy
        {
            AccentState = accentState,
            GradientColor = (blurOpacity << 24) | (backgroundColor & 0xFFFFFF)
        };

        int accentStructSize = Marshal.SizeOf(accent);
        IntPtr accentPtr = Marshal.AllocHGlobal(accentStructSize);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                SizeOfData = accentStructSize,
                Data = accentPtr
            };

            return SetWindowCompositionAttribute(handle, ref data) != 0;
        }
        finally
        {
            // a throw between the allocation and the call used to leak this buffer on every theme change
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    /// <summary>
    /// Returns the tint the compositor should blur, preferring the active theme brush over a fixed color
    /// </summary>
    private static uint GetBlurBackgroundColor()
    {
        if (Application.Current?.TryFindResource("ApplicationBackgroundBrush") is System.Windows.Media.SolidColorBrush brush)
        {
            // the accent policy expects the color packed as 0x00BBGGRR
            System.Windows.Media.Color color = brush.Color;
            return (uint)(color.R | (color.G << 8) | (color.B << 16));
        }

        return ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light
            ? LightFallbackBackgroundColor
            : DarkFallbackBackgroundColor;
    }

    /// <summary>
    /// Checks if a window should have acrylic blur enabled based on settings
    /// </summary>
    private static bool ShouldHaveAcrylicBlur(Window window)
    {
        // Matched by type, not by GetType().Name: a string comparison silently stops matching if a
        // window is renamed or subclassed, which made acrylic quietly disappear instead of failing loudly.
        return window switch
        {
            global::FluentFlyoutWPF.MainWindow => SettingsManager.Current.MediaFlyoutAcrylicWindowEnabled,
            global::FluentFlyoutWPF.Windows.NextUpWindow => SettingsManager.Current.NextUpAcrylicWindowEnabled,
            global::FluentFlyoutWPF.Windows.LockWindow => SettingsManager.Current.LockKeysAcrylicWindowEnabled,
            global::FluentFlyoutWPF.Windows.VolumeMixerWindow => SettingsManager.Current.VolumeMixerAcrylicWindowEnabled,
            _ => false
        };
    }
}