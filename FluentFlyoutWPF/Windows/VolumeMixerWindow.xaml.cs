// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

// Portions of this code are derived from:
// - gpkgpk/HideVolumeOSD: https://github.com/gpkgpk/HideVolumeOSD
//
// Copyright (c) 2022 gpkgpk
// Modifications copyright (c) 2026 The FluentFlyout Authors

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.ViewModels;
using MicaWPF.Controls;
using NLog;
using System.Windows;
using System.Windows.Media.Animation;
using static FluentFlyout.Classes.NativeMethods;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Interaction logic for VolumeMixerWindow.xaml
/// </summary>
public partial class VolumeMixerWindow : MicaWindow
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    public VolumeMixerViewModel ViewModel { get; } = new();
    public UserSettings UserSettings => SettingsManager.Current;

    private static IntPtr _nativeOsdElement = IntPtr.Zero;
    private static int _nativeOsdOriginalExStyle;
    private CancellationTokenSource _cts;
    // Nullable on purpose: the flyout degrades to its standalone layout instead of throwing when the
    // main window is not around (for example while the application is shutting down).
    private readonly MainWindow? _mainWindow;
    private readonly double _collapsedHeight = 50;
    private readonly double _normalWidth;
    private bool _isHiding = true;

    private long _lastFlyoutTime = 0;
    private readonly TimeSpan _flyoutCooldown = TimeSpan.FromMilliseconds(500);

    public VolumeMixerWindow()
    {
        DataContext = this;
        WindowHelper.SetNoActivate(this);
        InitializeComponent();
        WindowHelper.SetTopmost(this);
        CustomWindowChrome.CaptionHeight = 0;
        CustomWindowChrome.UseAeroCaptionButtons = false;
        CustomWindowChrome.GlassFrameThickness = new Thickness(0);

        _mainWindow = Application.Current?.MainWindow as MainWindow;
        _cts = new CancellationTokenSource();
        _normalWidth = Width;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.SessionVolumeChanged += OnSessionVolumeChanged;
    }

    // one day we might want to convert these to an interface
    public async void ShowFlyout(bool startExpanded = false)
    {
        if (FullscreenDetector.IsFullscreenApplicationRunning())
            return;

        long currentTime = Environment.TickCount64;

        if (currentTime - _lastFlyoutTime < _flyoutCooldown.TotalMilliseconds)
        {
            return;
        }

        _lastFlyoutTime = currentTime;

        if (_isHiding)
        {
            if (_nativeOsdElement == IntPtr.Zero)
            {
                _ = Task.Run(() =>
                {
                    HideVolumeOsd();
                });
            }

            _isHiding = false;
            if (SettingsManager.Current.VolumeMixerAcrylicWindowEnabled)
            {
                WindowBlurHelper.EnableBlur(this);
            }
            else
            {
                WindowBlurHelper.DisableBlur(this);
            }

            // refresh all data
            ViewModel.OnPollTick(null, EventArgs.Empty);

            // The media flyout is shown asynchronously: ShowMediaFlyout() awaits the media property read
            // before it becomes visible, so on a volume hotkey it is normally still invisible although it is
            // already on its way in. Stacking above it only needs its size and monitor, so the pending
            // reference is accepted instead of waiting for it - waiting here would drop this flyout onto the
            // taskbar whenever the media property read is slow.
            bool aboveMedia = SettingsManager.Current.VolumeControlAboveMediaFlyout
                && SettingsManager.Current.MediaFlyoutEnabled
                && _mainWindow != null
                && _mainWindow.GetActiveMediaSession() != null;

            if (aboveMedia)
            {
                Width = _mainWindow!.Width;
                _mainWindow.OpenAnimation(this, aboveReference: _mainWindow, reserveNativeVolumeOsdSpace: true, referenceMayBeHidden: !_mainWindow.IsVisible);
            }
            else
            {
                Width = _normalWidth;
                _mainWindow?.OpenAnimation(this, alwaysBottom: true);
            }

            Show();
            WindowHelper.SetTopmost(this);

            _ = Task.Run(() =>
            {
                Thread.Sleep(MainWindow.getDuration());
                Dispatcher.Invoke(() =>
                {
                    if (startExpanded) ViewModel.IsExpanded = true;
                });
            });
        }
        else
        {
            // only expand if the flyout isn't expanded already
            if (startExpanded) ViewModel.IsExpanded = true;
        }

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(100, token); // check if mouse is over every 100ms
                // update master volume again because it can be slow to update when coming from a hardware key press
                ViewModel.SyncMasterFromDevice();

                bool mouseOverThis = WindowHelper.IsMouseOverWindow(this);
                bool mouseOverMedia = SettingsManager.Current.VolumeControlAboveMediaFlyout
                    && _mainWindow is { Visibility: Visibility.Visible }
                    && WindowHelper.IsMouseOverWindow(_mainWindow); // sync with media flyout

                if (!mouseOverThis && !mouseOverMedia)
                {
                    await Task.Delay(SettingsManager.Current.VolumeControlDuration, token);

                    mouseOverThis = WindowHelper.IsMouseOverWindow(this);
                    mouseOverMedia = SettingsManager.Current.VolumeControlAboveMediaFlyout
                        && _mainWindow is { Visibility: Visibility.Visible }
                        && WindowHelper.IsMouseOverWindow(_mainWindow);

                    if (!mouseOverThis && !mouseOverMedia)
                    {
                        _mainWindow?.CloseAnimation(this);
                        _isHiding = true;
                        await Task.Delay(MainWindow.getDuration());
                        if (_isHiding == false) return;

                        WindowHelper.SetVisibility(this, false);
                        ViewModel.IsExpanded = false;
                        break;
                    }
                }
            }
        }
        catch (TaskCanceledException)
        {
            // do nothing
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VolumeMixerViewModel.IsExpanded))
        {
            AnimateExpandCollapse(ViewModel.IsExpanded);
        }
    }

    private void OnSessionVolumeChanged(object? sender, EventArgs e)
    {
        _mainWindow?.taskbarWindow?.RefreshAppVolumeTooltip();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        _cts.Dispose();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.SessionVolumeChanged -= OnSessionVolumeChanged;
        ViewModel.Dispose();
        base.OnClosed(e);
    }

    // derived from gpkgpk/HideVolumeOSD: https://github.com/gpkgpk/HideVolumeOSD
    private static void HideVolumeOsd()
    {
        // find widget in XAML
        // the cursor (hwndChildAfter) must advance on every iteration, otherwise a window that
        // fails one of the checks below is matched again forever and this loop never returns
        IntPtr hwndXamlIsland = IntPtr.Zero, hwndOsd = IntPtr.Zero;
        const int maxCandidates = 256;
        for (int i = 0; i < maxCandidates; i++)
        {
            hwndXamlIsland = FindWindowEx(IntPtr.Zero, hwndXamlIsland, "XamlExplorerHostIslandWindow", null);
            if (hwndXamlIsland == IntPtr.Zero)
            {
                break;
            }

            hwndOsd = FindWindowEx(hwndXamlIsland, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", "DesktopWindowXamlSource");
            if (hwndOsd == IntPtr.Zero)
            {
                continue;
            }

            // check if the child window has the expected class name and title
            IntPtr hwndInputClass = FindWindowEx(hwndOsd, IntPtr.Zero, "Windows.UI.Input.InputSite.WindowClass", null);
            if (hwndInputClass == IntPtr.Zero)
            {
                hwndOsd = IntPtr.Zero;
                continue;
            }

            ShowWindow(hwndInputClass, 9); // SW_RESTORE
            if (GetWindowRect(hwndInputClass, out RECT rect))
            {
                if (rect.Top != 0 || rect.Left != 0 || rect.Bottom != 0 || rect.Right != 0)
                {
                    break;
                }
            }

            hwndOsd = IntPtr.Zero;
        }

        if (hwndOsd == IntPtr.Zero)
        {
            Logger.Warn("OSD window not found.");
            return;
        }

        // the parent owns the hit-test region on the desktop
        _nativeOsdElement = hwndXamlIsland;
        _nativeOsdOriginalExStyle = GetWindowLong(_nativeOsdElement, GWL_EXSTYLE);
        SetWindowLong(_nativeOsdElement, GWL_EXSTYLE,
            _nativeOsdOriginalExStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        SetWindowPos(_nativeOsdElement, 0, -99999, -99999, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        ShowWindow(_nativeOsdElement, SW_MINIMIZE);
        Logger.Info("Successfully hid volume OSD.");
    }

    public static void ShowVolumeOsd()
    {
        if (_nativeOsdElement == IntPtr.Zero)
        {
            Logger.Warn("Did not try to restore OSD because it was either not found or was not hidden.");
            return;
        }

        SetWindowLong(_nativeOsdElement, GWL_EXSTYLE, _nativeOsdOriginalExStyle);
        SetWindowPos(_nativeOsdElement, 0, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        ShowWindow(_nativeOsdElement, SW_RESTORE);
        _nativeOsdElement = IntPtr.Zero;
        Logger.Info("Successfully restored volume OSD.");
    }

    private void AnimateExpandCollapse(bool expand)
    {
        int msDuration = MainWindow.getDuration();
        var easing = msDuration > 0 ? _mainWindow?.getEasingStyle(true) : null;
        var duration = new Duration(TimeSpan.FromMilliseconds(msDuration > 0 ? msDuration / 1.4 : 1));

        bool isTop = false;

        // check if the media flyout is at the top or bottom of the screen if applicable
        if (SettingsManager.Current.VolumeControlAboveMediaFlyout)
        {
            isTop = SettingsManager.Current.Position switch
            {
                3 or 4 or 5 => true,
                _ => false
            };
        }

        double expandedHeight;
        if (expand)
        {
            SessionsExpanded.Visibility = Visibility.Visible;
            SessionsSeparator.Visibility = Visibility.Visible;
            SessionsPanel.UpdateLayout();
        }

        // measure desired size
        SessionsExpanded.Measure(new Size(ActualWidth, double.PositiveInfinity));
        expandedHeight = _collapsedHeight + Math.Min(SessionsExpanded.DesiredSize.Height, 220);

        double targetHeight = expand ? expandedHeight : _collapsedHeight;
        double currentHeight = ActualHeight;
        double heightDelta = targetHeight - currentHeight;

        // When at the top, chevron points down (0°) when collapsed and up (180°) when expanded.
        // When at the bottom, chevron points up (180°) when expanded and down (0°) when collapsed.
        var chevronAnimation = new DoubleAnimation
        {
            To = isTop ? (expand ? 0 : 180) : (expand ? 180 : 0),
            Duration = duration,
            EasingFunction = easing
        };
        Dispatcher.Invoke(() =>
        {
            ChevronRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, chevronAnimation);
        });

        var heightAnimation = new DoubleAnimation
        {
            From = currentHeight,
            To = targetHeight,
            Duration = duration,
            EasingFunction = easing
        };

        // When at the top, the window grows downward so Top stays fixed.
        // When at the bottom, the window grows upward so Top shifts up by heightDelta.
        var topAnimation = new DoubleAnimation
        {
            From = Top,
            To = isTop ? Top : Top - heightDelta,
            Duration = duration,
            EasingFunction = easing
        };

        if (!expand)
        {
            heightAnimation.Completed += (s, e) =>
            {
                SessionsExpanded.Visibility = Visibility.Collapsed;
                SessionsSeparator.Visibility = Visibility.Collapsed;
            };
        }

        Dispatcher.Invoke(() =>
        {
            BeginAnimation(TopProperty, topAnimation);
            BeginAnimation(HeightProperty, heightAnimation);
        });
    }
}