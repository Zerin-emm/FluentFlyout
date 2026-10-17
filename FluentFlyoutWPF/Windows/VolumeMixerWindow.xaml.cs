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

    /// <summary>
    /// Whether the current position was computed against the media flyout (stacked above it) rather than
    /// as a standalone bottom flyout. Used to avoid re-positioning a flyout that is already stacked when
    /// the media flyout completes its own (asynchronous) show.
    /// </summary>
    private bool _stackedAboveMedia;

    private long _lastFlyoutTime = 0;
    private readonly TimeSpan _flyoutCooldown = TimeSpan.FromMilliseconds(500);

    public VolumeMixerWindow()
    {
        DataContext = this;
        WindowHelper.SetNoActivate(this);
        InitializeComponent();

        // The handle has to exist before the first ShowFlyout: everything that positions or tints this
        // window (OpenAnimation, WindowHelper.*, WindowBlurHelper.*) goes through Win32 and silently does
        // nothing while the window has no HWND. This window is never shown by itself, so without this the
        // first flyout of every process would be placed at the system default position and stay unblurred.
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();

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

    /// <summary>
    /// Shows the volume flyout.
    /// </summary>
    /// <param name="startExpanded">Whether the per-app session list should be expanded.</param>
    /// <param name="mediaFlyoutExpected">
    /// True when the caller is also summoning the media flyout for this same trigger (a volume hotkey that
    /// is not excluded from the media flyout). That intention cannot be read from the media flyout itself,
    /// because <c>ShowMediaFlyout</c> is asynchronous and only becomes visible after it has read the media
    /// properties.
    /// </param>
    public async void ShowFlyout(bool startExpanded = false, bool mediaFlyoutExpected = false)
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

            _stackedAboveMedia = ShouldStackAboveMediaFlyout(mediaFlyoutExpected);

            if (_stackedAboveMedia)
            {
                Width = _mainWindow!.Width;
                _mainWindow.OpenAnimation(this, aboveReference: _mainWindow, reserveNativeVolumeOsdSpace: true, referenceMayBeHidden: !_mainWindow.IsVisible);
            }
            else
            {
                Width = _normalWidth;
                // No media flyout to sit above: go just above the taskbar, centred on the taskbar widget
                // (which is not the middle of the screen on most layouts).
                _mainWindow?.OpenAnimation(this, alwaysBottom: true, anchorToTaskbarWidget: true);
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
        var currentCts = new CancellationTokenSource();
        _cts = currentCts;
        var token = currentCts.Token;

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
                        _stackedAboveMedia = false; // the next show recomputes its position from scratch
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
        catch (Exception ex)
        {
            // This loop reads the audio device and every session once per 100 ms. NAudio throws
            // COMException when the device is unplugged or a session disappears in between, and this is an
            // async void method on the UI thread: an escaping exception would terminate the process. Close
            // the flyout instead, which is also what the user sees when the audio device goes away.
            Logger.Error(ex, "Volume flyout update loop failed, hiding the flyout");

            if (ReferenceEquals(_cts, currentCts))
            {
                _isHiding = true;
                WindowHelper.SetVisibility(this, false);
            }
        }
    }

    /// <summary>
    /// Whether this flyout belongs above the media flyout right now.
    /// </summary>
    /// <remarks>
    /// The setting alone is not enough: "above the media flyout" is a position relative to a window that
    /// may not be there at all. The agreed semantics are:
    /// <list type="bullet">
    /// <item>volume keys not excluded: the volume key summons the media flyout too (<c>mediaFlyoutExpected</c>
    /// is true) and this flyout is stacked above its real position;</item>
    /// <item>volume keys excluded, and the taskbar wheel path: no media flyout is summoned, so this flyout
    /// goes just above the taskbar instead of reserving a slot that nothing fills (that reservation was the
    /// "empty gap exactly the size of the media flyout" bug).</item>
    /// </list>
    /// The one deliberate refinement: when the media flyout happens to be on screen already (for example
    /// "always display" or a flyout that has not auto-hidden yet), this flyout still stacks above it even
    /// with volume keys excluded, because placing it at the taskbar would draw it on top of the other one.
    /// </remarks>
    private bool ShouldStackAboveMediaFlyout(bool mediaFlyoutExpected)
    {
        bool settingEnabled = SettingsManager.Current.VolumeControlAboveMediaFlyout;
        bool mediaFlyoutEnabled = SettingsManager.Current.MediaFlyoutEnabled;
        bool mediaFlyoutDisplayed = _mainWindow?.IsMediaFlyoutDisplayed ?? false;
        bool mediaFlyoutPresent = mediaFlyoutExpected || mediaFlyoutDisplayed;

        bool stack = settingEnabled
            && mediaFlyoutEnabled
            && _mainWindow != null
            && mediaFlyoutPresent
            && _mainWindow.GetActiveMediaSession() != null;

        // One line per decision: this is what makes a report like "the volume flyout sits too low/high"
        // diagnosable from the log file (%APPDATA%\FluentFlyout\logs.*.txt) without a debugger. The session
        // check is the last term, so "everything true but not stacked" means there was no media session.
        Logger.Info($"Volume flyout placement: {(stack ? "above the media flyout" : "above the taskbar")} " +
                    $"(setting={settingEnabled}, mediaFlyoutEnabled={mediaFlyoutEnabled}, " +
                    $"mediaFlyoutExpected={mediaFlyoutExpected}, mediaFlyoutDisplayed={mediaFlyoutDisplayed})");

        return stack;
    }

    /// <summary>
    /// Moves an already visible volume flyout back above the media flyout, for the case where the volume
    /// flyout was summoned on its own (taskbar wheel) and the media flyout then appears underneath it.
    /// </summary>
    /// <remarks>
    /// Does nothing when this flyout is hidden or closing, when stacking is disabled, or when it is already
    /// stacked above the media flyout - that last case is the normal "volume key shows both flyouts" flow,
    /// where this flyout was positioned against the media flyout before it appeared and must not be
    /// animated a second time.
    /// </remarks>
    public void MoveAboveMediaFlyoutIfVisible()
    {
        if (_isHiding || !IsVisible || _stackedAboveMedia)
            return;

        if (!ShouldStackAboveMediaFlyout(mediaFlyoutExpected: true))
            return;

        _stackedAboveMedia = true;

        // The reference is visible here, so its real size is the one to stack against.
        Width = _mainWindow!.Width;
        _mainWindow.OpenAnimation(this, aboveReference: _mainWindow, reserveNativeVolumeOsdSpace: true, referenceMayBeHidden: false);
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