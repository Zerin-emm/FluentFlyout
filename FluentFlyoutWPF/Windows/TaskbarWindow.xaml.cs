// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Utils;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using static FluentFlyout.Classes.NativeMethods;

namespace FluentFlyout.Windows;

/// <summary>
/// Interaction logic for TaskbarWindow.xaml
/// </summary>
public partial class TaskbarWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private const double SmallTaskbarDetectionThreshold = 40;

    private readonly DispatcherTimer _timer;
    private readonly int _nativeWidgetsPadding = 216;
    private readonly double _scale = 0.9;

    // The UI Automation queries run on a worker thread. These are the budgets the UI thread waits for
    // them before giving up on a taskbar element whose provider has stopped responding.
    private static readonly TimeSpan AutomationFindTimeout = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan AutomationBoundsTimeout = TimeSpan.FromMilliseconds(500);

    private IntPtr _trayHandle;
    private AutomationElement? _widgetElement;
    private AutomationElement? _trayElement;
    private AutomationElement? _taskbarFrameElement;
    // reference to main window for flyout functions
    private MainWindow? _mainWindow;
    private int _lastSelectedMonitor = -1;
    private IntPtr _lastTaskbarHandle;
    private bool _positionUpdateInProgress;
    private static bool _suppressedMessagesLogged;
    private bool _isClosing;
    private readonly Dictionary<string, Task> _pendingAutomationTasks = [];

    private GlobalSystemMediaTransportControlsSessionPlaybackStatus? _lastPlaybackStatus;
    private DispatcherTimer? _autoHideTimer;

    public TaskbarWindow()
    {
        WindowHelper.SetNoActivate(this);
        InitializeComponent();
        WindowHelper.SetTopmost(this);

        // Set DataContext for bindings
        DataContext = SettingsManager.Current;

        _timer = new DispatcherTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(1500); // slow auto-update for display changes
        _timer.Tick += (s, e) => UpdatePosition();
        _timer.Start();

        Show();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource source = (HwndSource)PresentationSource.FromDependencyObject(this);
        source.AddHook(WindowProc);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_DPICHANGED or WM_DPICHANGED_AFTERPARENT)
        {
            // WPF processes WM_DPICHANGED itself. Refresh placement after that layout
            // pass; PMv2 child windows receive the AFTERPARENT variant instead.
            Dispatcher.BeginInvoke(() =>
            {
                InvalidateMeasure();
                InvalidateArrange();
                InvalidateVisual();
                UpdateLayout();
                UpdatePosition();
            }, DispatcherPriority.Loaded);
        }

        // Some interface mods may collect information from all windows associated with the taskbar,
        // causing the widget and the entire taskbar to freeze.
        // For example, Nilesoft Shell and "Click on empty taskbar space" from Windhawk.
        // Therefore, we are preventing the propagation of this message.
        // Also prevents the widget from blocking taskbar's message processing, which is another source of freezes.
        //
        // Trade-off: suppressing WM_GETOBJECT keeps the widget out of the taskbar's UI Automation tree,
        // so screen readers and automation tools cannot see it. That is accepted because the freeze it
        // prevents happens on machines that never asked for either, and the tools cannot be detected
        // reliably (they inject into Explorer, and there is no supported way to query them), so the
        // filtering is unconditional. The first suppression is logged so a report about missing
        // accessibility information can be traced back to this branch.
        switch (msg)
        {
            case 0x003D: // WM_GETOBJECT (Sent by Microsoft UI Automation to obtain information about an accessible object contained in a server application)
            case 0x0018: // WM_SHOWWINDOW
            case 0x0046: // WM_WINDOWPOSCHANGING - Triggers during alt-tabs, window changes
            case 0x0083: // WM_NCCALCSIZE - Can trigger layout storms
            case 0x0281: // WM_IME_SETCONTEXT - IME conflicts
            case 0x0282: // WM_IME_NOTIFY
                if (!_suppressedMessagesLogged)
                {
                    _suppressedMessagesLogged = true;
                    Logger.Info("Suppressing taskbar widget messages (WM_GETOBJECT etc.) to avoid taskbar freezes; " +
                        "the widget is not visible to UI Automation as a result");
                }

                handled = true;
                return IntPtr.Zero;

                // Handle other known harmless messages that are sent when FluentFlyout starts, Windows locks, etc.
                // Needs testing
                //case 0x0047:
                //case 0x02B1:
                //case 0x001E:
                //case 0x0164:
                //case 0xC25F:
                //    handled = true;
                //    return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SetupWindow();
        _mainWindow = Application.Current?.MainWindow as MainWindow;
        Widget.SetMainWindow(_mainWindow);
    }

    private IntPtr GetSelectedTaskbarHandle(out bool isMainTaskbarSelected)
    {
        var monitors = MonitorUtil.GetMonitors();
        var selectedMonitor = monitors[Math.Clamp(SettingsManager.Current.TaskbarWidgetSelectedMonitor, 0, monitors.Count - 1)];
        isMainTaskbarSelected = true;

        // Get the main taskbar and check if it is on the selected monitor.
        var mainHwnd = FindWindow("Shell_TrayWnd", null);
        if (MonitorUtil.GetMonitor(mainHwnd).deviceId == selectedMonitor.deviceId)
            return mainHwnd;

        if (monitors.Count == 1)
            return mainHwnd;

        isMainTaskbarSelected = false;
        if (monitors.Count == 2)
        {
            var hwnd = FindWindow("Shell_SecondaryTrayWnd", null);
            if (MonitorUtil.GetMonitor(hwnd).deviceId == selectedMonitor.deviceId)
            {
                return hwnd;
            }
            else
            {
                isMainTaskbarSelected = true;
                return mainHwnd;
            }
        }

        // If there are more than two monitors, we will need to enumerate all existing windows
        // to find all Shell_SecondaryTrayWnd among them.

        IntPtr secondHwnd = IntPtr.Zero;
        StringBuilder className = new(256); // 256 is the maximum class name length
        IntPtr checkWindowClass(IntPtr wnd)
        {
            var len = GetClassName(wnd, className, className.Capacity);
            if (className.Equals("Shell_SecondaryTrayWnd"))
            {
                if (MonitorUtil.GetMonitor(wnd).deviceId == selectedMonitor.deviceId)
                {
                    return wnd;
                }
            }
            return IntPtr.Zero;
        }

        // Get the threadId of the main taskbar and check all windows created in the same thread.
        // This is very fast, but in some cases Shell_TrayWnd and other Shell_SecondaryTrayWnd's may be created in different threads.
        // Actually, I couldn't achieve that kind of behavior.
        if (mainHwnd != IntPtr.Zero)
        {
            uint threadId = GetWindowThreadProcessId(mainHwnd, IntPtr.Zero);
            EnumThreadWindows(threadId, (wnd, param) =>
            {
                secondHwnd = checkWindowClass(wnd);
                if (secondHwnd != IntPtr.Zero)
                    return false; // stop

                return true;
            }, IntPtr.Zero);

            if (secondHwnd != IntPtr.Zero)
                return secondHwnd;
        }

        // If for some reason the taskbars were created in different threads or simply could not be found,
        // we try to find them among all existing windows.
        EnumWindows((wnd, param) =>
        {
            secondHwnd = checkWindowClass(wnd);
            if (secondHwnd != IntPtr.Zero)
                return false; // stop

            return true;
        }, IntPtr.Zero);

        if (secondHwnd != IntPtr.Zero)
            return secondHwnd;

        // Logger.Debug($"No taskbar found on the selected monitor. Using the main taskbar.");
        isMainTaskbarSelected = true;
        return mainHwnd;
    }

    private void SetupWindow()
    {
        try
        {
            var interop = new WindowInteropHelper(this);
            IntPtr taskbarWindowHandle = interop.Handle;

            //Background = _hitTestTransparent; // ensures that non-content areas also trigger MouseEnter event

            IntPtr taskbarHandle = GetSelectedTaskbarHandle(out bool isMainTaskbarSelected);
            ResetTaskbarCachesIfHandleChanged(taskbarHandle);

            if (!AttachToTaskbar(taskbarWindowHandle, taskbarHandle))
            {
                // The taskbar is not up yet (the widget window is created before Explorer finishes
                // starting). UpdatePosition runs on the timer below and attaches once it appears.
                return;
            }

            _ = CalculateAndSetPositionAsync(taskbarHandle, taskbarWindowHandle, isMainTaskbarSelected);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Taskbar Widget error during setup");
        }
    }

    /// <summary>
    /// Turns the widget into a child window of the taskbar so it moves, clips and hides with it
    /// </summary>
    /// <returns>True when the widget is attached to the given taskbar</returns>
    /// <remarks>
    /// SetParent must never be called with a NULL parent: that re-parents the window to the desktop
    /// instead of doing nothing, which leaves the widget floating on top of everything until Explorer
    /// comes back. The old code did exactly that when the window was created before the taskbar.
    /// </remarks>
    private bool AttachToTaskbar(IntPtr taskbarWindowHandle, IntPtr taskbarHandle)
    {
        if (taskbarHandle == IntPtr.Zero)
        {
            Logger.Debug("The taskbar is not available yet, the widget stays unattached and will retry");
            return false;
        }

        // This prevents the window from trying to float above the taskbar as a separate entity
        int style = GetWindowLong(taskbarWindowHandle, GWL_STYLE);
        style = (style & ~WS_POPUP) | WS_CHILD;
        SetWindowLong(taskbarWindowHandle, GWL_STYLE, style);

        SetParent(taskbarWindowHandle, taskbarHandle);

        // SetParent returns the previous parent, which is legitimately NULL on the first attach, so the
        // result is verified through GetParent instead.
        if (GetParent(taskbarWindowHandle) != taskbarHandle)
        {
            Logger.Warn("Failed to attach the Taskbar Widget to the taskbar, will retry");
            return false;
        }

        return true;
    }

    private void UpdateWindowRegion(IntPtr windowHandle, params Rect[] rects)
    {
        IntPtr rgn = CreateRectRgn(0, 0, 0, 0);
        foreach (var r in rects)
        {
            // make sure rect is not empty - happens when setting elements to collapsed
            if (r == Rect.Empty)
                continue;

            IntPtr newRgn = CreateRectRgn((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom);
            if (newRgn == IntPtr.Zero)
            {
                Logger.Error($"Taskbar Widget error during CreateRectRgn({(int)r.Left}, {(int)r.Top}, {(int)r.Right}, {(int)r.Bottom}).");
                goto on_error;
            }

            if (CombineRgn(rgn, rgn, newRgn, 2 /*RGN_OR*/) == 0)
            {
                Logger.Error($"Taskbar Widget error during CombineRgn. Combined regions: {string.Join(", ", rects.Select(i => $"RECT({(int)i.Left}, {(int)i.Top}, {(int)i.Right}, {(int)i.Bottom})"))}");
                DeleteObject(newRgn);
                goto on_error;
            }

            DeleteObject(newRgn);
        }

        if (SetWindowRgn(windowHandle, rgn, true) == 0)
        {
            Logger.Error($"Taskbar Widget error during SetWindowRgn.");
            goto on_error;
        }

        // Simple debugging to display the window region:
#if false
        var whiteRect = WidgetCanvas.Children.Cast<FrameworkElement>().FirstOrDefault(e => e.Name == "test_border");
        if (whiteRect == null)
        {
            whiteRect = new System.Windows.Shapes.Rectangle() { Name = "test_border", Width = 20000, Height = 20000, Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black) };
            WidgetCanvas.Children.Add(whiteRect);
            Canvas.SetLeft(whiteRect, -10000);
            Canvas.SetTop(whiteRect, -10000);
        }
#endif

        return;

on_error:

// All regions that were not sent without errors to SetWindowRgn must be destroyed manually
        DeleteObject(rgn);
        if (SetWindowRgn(windowHandle, IntPtr.Zero, true) == 0)
            Logger.Error("Taskbar Widget error during window region reset.");
    }

    private void UpdatePosition()
    {
        if (_isClosing || MainWindow.ExplorerRestarting)
        {
            // Explorer is restarting -- do NOTHING
            return;
        }

        if (!SettingsManager.Current.TaskbarWidgetEnabled)
            return;

        try
        {
            var interop = new WindowInteropHelper(this);
            IntPtr taskbarHandle = GetSelectedTaskbarHandle(out bool isMainTaskbarSelected);
            ResetTaskbarCachesIfHandleChanged(taskbarHandle);

            if (interop.Handle == IntPtr.Zero)
            {
                if (MainWindow.ExplorerRestarting)
                {
                    Logger.Info("Skipping TaskbarWindow recovery during Explorer restart");
                    return;
                }

                _timer.Stop();

                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _mainWindow?.RecreateTaskbarWindow();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Failed to signal MainWindow to recover Taskbar Widget window");
                    }
                }, DispatcherPriority.Background);

                return;
            }

            // If the Taskbar was not found during initialization or another taskbar was selected,
            // then we need to set the Taskbar as the Parent here.
            if (GetParent(interop.Handle) != taskbarHandle && !AttachToTaskbar(interop.Handle, taskbarHandle))
            {
                // No taskbar to attach to yet - retry on the next timer tick instead of re-parenting
                // the widget to the desktop.
                return;
            }

            if (taskbarHandle != IntPtr.Zero && interop.Handle != IntPtr.Zero)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _ = CalculateAndSetPositionAsync(taskbarHandle, interop.Handle, isMainTaskbarSelected);
                }, DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Taskbar Widget error during position update");
        }
    }

    private void ResetTaskbarCachesIfHandleChanged(IntPtr taskbarHandle)
    {
        if (_lastTaskbarHandle == taskbarHandle)
            return;

        _lastTaskbarHandle = taskbarHandle;
        _trayHandle = IntPtr.Zero;
        _widgetElement = null;
        _trayElement = null;
        _taskbarFrameElement = null;
        _pendingAutomationTasks.Clear();
    }

    private async Task CalculateAndSetPositionAsync(IntPtr taskbarHandle, IntPtr taskbarWindowHandle, bool isMainTaskbarSelected)
    {
        // Prevent overlapping updates - if a previous update is still running
        // (e.g. waiting for an automation query timeout), skip this tick.
        if (_positionUpdateInProgress)
            return;
        _positionUpdateInProgress = true;

        try
        {
            // get DPI scaling
            double dpiScale = GetDpiForWindow(taskbarHandle) / 96.0;

            // Guard against invalid DPI (e.g. during explorer restart when handle is stale)
            if (dpiScale <= 0)
                return;

            // Get Taskbar dimensions
            RECT taskbarRect;

            if (!SettingsManager.Current.LegacyTaskbarWidthEnabled)
            {
                // first, try to find the Taskbar.TaskbarFrame element in the XAML
                // this should give us the actual bounds of the taskbar, excluding invisible margins on some Windows configurations
                (bool success, Rect result) = await GetTaskbarFrameRectAsync(taskbarHandle);
                if (success)
                {
                    taskbarRect = new RECT
                    {
                        Left = (int)result.Left,
                        Top = (int)result.Top,
                        Right = (int)result.Right,
                        Bottom = (int)result.Bottom
                    };
                }
                else
                {
                    // fallback to GetWindowRect if we fail to get the frame bounds for some reason
                    GetWindowRect(taskbarHandle, out taskbarRect);
                }
            }
            else
            {
                // legacy method - GetWindowRect on the entire taskbar, which includes invisible margins on some Windows configurations
                GetWindowRect(taskbarHandle, out taskbarRect);
            }

            int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
            int taskbarWidth = taskbarRect.Right - taskbarRect.Left;

            // Vertical taskbar support: rotate and reposition widget when taskbar is taller than wide
            bool isVertical = taskbarHeight > taskbarWidth;
            double taskbarCrossSize = (isVertical ? taskbarWidth : taskbarHeight) / dpiScale;
            bool isSmallTaskbar = taskbarCrossSize < SmallTaskbarDetectionThreshold;
            int containerWidth = taskbarWidth;
            int containerHeight = taskbarHeight;

            Widget.SetSmallTaskbarMode(isSmallTaskbar);
            TaskbarVisualizer.SetSmallTaskbarMode(isSmallTaskbar);

            // Following SetWindowPos will set the position relative to the parent window,
            // so those coordinates need to be converted.
            POINT containerPos = new() { X = taskbarRect.Left, Y = taskbarRect.Top };
            ScreenToClient(taskbarHandle, ref containerPos);

            // Apply using SetWindowPos (Bypassing WPF layout engine)
            SetWindowPos(taskbarWindowHandle, 0,
                     containerPos.X, containerPos.Y,
                     containerWidth, containerHeight,
                     SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS | SWP_SHOWWINDOW);
            var wRect = await PositionWidgetAsync(taskbarHandle, taskbarRect, dpiScale, isMainTaskbarSelected, isVertical);
            var vRect = PositionVisualizer(taskbarHandle, taskbarRect, dpiScale, isMainTaskbarSelected, isVertical);

            UpdateWindowRegion(taskbarWindowHandle, wRect, vRect);

            _lastSelectedMonitor = SettingsManager.Current.TaskbarWidgetSelectedMonitor;
        }
        catch (Exception ex)
        {
            // This method is dispatched fire-and-forget, so nothing above it can observe a throw
            Logger.Error(ex, "Taskbar Widget error during position calculation");
        }
        finally
        {
            _positionUpdateInProgress = false;
        }
    }

    private async Task<Rect> PositionWidgetAsync(IntPtr taskbarHandle, RECT taskbarRect, double dpiScale, bool isMainTaskbarSelected, bool isVertical)
    {
        if (!SettingsManager.Current.TaskbarWidgetEnabled)
            return Rect.Empty;

        Widget.SetVerticalMode(isVertical);

        // Calculate widget size
        var (logicalWidth, logicalHeight) = Widget.CalculateSize(dpiScale);

        int physicalWidth = (int)(logicalWidth * dpiScale * _scale);
        int physicalHeight = (int)(logicalHeight * dpiScale);

        int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        int taskbarWidth = taskbarRect.Right - taskbarRect.Left;

        // Apply orientation transform
        Widget.LayoutTransform = isVertical ? new System.Windows.Media.RotateTransform(90) : null;
        Widget.RenderTransform = System.Windows.Media.Transform.Identity;

        // On a vertical taskbar the widget is rotated 90°, so the axes flip:
        //   primarySize = taskbarHeight, positioning runs along Y
        //   crossSize   = taskbarWidth,  widget is centered along X
        //   physicalWidth  = visual extent along primary axis (logical width = visual height after rotation)
        //   physicalHeight = visual extent along cross axis   (logical height = visual width after rotation)
        int primarySize = isVertical ? taskbarHeight : taskbarWidth;
        int crossSize = isVertical ? taskbarWidth : taskbarHeight;

        // Center on the cross axis; both orientations use physicalHeight for the cross dimension
        int crossPos = (crossSize - physicalHeight) / 2;

        // Primary axis position (calculated per-case below)
        int primaryPos = 0;

        switch (SettingsManager.Current.TaskbarWidgetPosition)
        {
            case 0: // near start (left for horizontal, top for vertical)
                primaryPos = 20;

                if (SettingsManager.Current.TaskbarVisualizerEnabled && SettingsManager.Current.TaskbarVisualizerPosition == 0)
                    primaryPos += (int)(TaskbarVisualizer.Width * dpiScale) + 4;

                if (!SettingsManager.Current.TaskbarWidgetPadding)
                    break;

                // automatic widget padding to the start
                try
                {
                    // find widget button in XAML
                    (bool found, Rect nativeWidgetRect) = await GetTaskbarWidgetRectAsync(taskbarHandle);

                    // Accept only if the native Widgets button is in the start half of the taskbar
                    bool inStartHalf = isVertical
                        ? nativeWidgetRect.Bottom < (taskbarRect.Top + taskbarRect.Bottom) / 2.0
                        : nativeWidgetRect.Right < (taskbarRect.Left + taskbarRect.Right) / 2.0;

                    if (found && inStartHalf)
                    {
                        // Convert absolute screen position to relative position within taskbar
                        primaryPos = isVertical
                            ? (int)(nativeWidgetRect.Bottom - taskbarRect.Top) + 2
                            : (int)(nativeWidgetRect.Right - taskbarRect.Left) + 2;
                    }
                }
                catch (Exception ex)
                {
                    // fallback to default padding
                    Logger.Warn(ex, "Failed to get Widgets button position.");
                    primaryPos += _nativeWidgetsPadding + 2;
                }
                break;

            case 1: // center of the taskbar
                primaryPos = (primarySize - physicalWidth) / 2;

                if (SettingsManager.Current.TaskbarVisualizerEnabled)
                    if (SettingsManager.Current.TaskbarVisualizerPosition == 0)
                        primaryPos += (int)(TaskbarVisualizer.Width * dpiScale) / 2 + 4;
                    else
                        primaryPos -= (int)(TaskbarVisualizer.Width * dpiScale) / 2 - 4;
                break;

            case 2: // near end (right for horizontal, bottom for vertical)
                try
                {
                    if (SettingsManager.Current.TaskbarVisualizerEnabled && SettingsManager.Current.TaskbarVisualizerPosition == 1)
                        primaryPos -= (int)(TaskbarVisualizer.Width * dpiScale) - 4;

                    // Horizontal only: try to position next to native Widgets button on the end side
                    if (!isVertical && SettingsManager.Current.TaskbarWidgetPadding)
                    {
                        try
                        {
                            // find widget button in XAML
                            (bool found, Rect nativeWidgetRect) = await GetTaskbarWidgetRectAsync(taskbarHandle);

                            // make sure it's on the right side, otherwise ignore (widget might be to the left)
                            if (found && nativeWidgetRect.Left > (taskbarRect.Left + taskbarRect.Right) / 2.0)
                            {
                                // Convert absolute screen position to relative position within taskbar
                                primaryPos += (int)(nativeWidgetRect.Left - taskbarRect.Left) - 1 - physicalWidth;
                                break;
                            }
                        }
                        catch (Exception ex) // catch exception when getting widget position
                        {
                            Logger.Warn(ex, "Failed to get Widgets button position.");
                        }
                    }

                    // try to position next to system tray
                    if (!isMainTaskbarSelected)
                    {
                        // find secondary tray with automation
                        (bool found, Rect trayRect) = await GetSystemTrayRectAsync(taskbarHandle);

                        if (found)
                        {
                            // Convert absolute screen position to relative position within taskbar
                            double trayOffset = isVertical
                                ? trayRect.Top - taskbarRect.Top
                                : trayRect.Left - taskbarRect.Left;
                            primaryPos += (int)trayOffset - physicalWidth - (isVertical ? 2 : 1);
                            break;
                        }
                    }
                    else
                    {
                        // Primary taskbar: for vertical, try automation first (more reliable on ExplorerPatcher)
                        if (isVertical)
                        {
                            (bool trayFound, Rect trayAutomationRect) = await GetSystemTrayRectAsync(taskbarHandle);
                            if (trayFound && trayAutomationRect.Top >= taskbarRect.Top)
                            {
                                primaryPos += (int)(trayAutomationRect.Top - taskbarRect.Top) - physicalWidth - 2;
                                break;
                            }
                        }

                        // Primary taskbar: TrayNotifyWnd (original approach for horizontal, fallback for vertical)
                        if (_trayHandle == IntPtr.Zero || _lastSelectedMonitor != SettingsManager.Current.TaskbarWidgetSelectedMonitor)
                            _trayHandle = FindWindowEx(taskbarHandle, IntPtr.Zero, "TrayNotifyWnd", null);

                        if (_trayHandle != IntPtr.Zero)
                        {
                            GetWindowRect(_trayHandle, out RECT trayWndRect);
                            // Convert absolute screen position to relative position within taskbar
                            double trayOffset = isVertical
                                ? trayWndRect.Top - taskbarRect.Top
                                : trayWndRect.Left - taskbarRect.Left;

                            // For vertical: validate the tray is in the lower half of the taskbar
                            if (!isVertical || trayOffset > taskbarHeight / 2)
                            {
                                primaryPos += (int)trayOffset - physicalWidth - (isVertical ? 2 : 6); // trayOffset isn't 100% accurate, so we subtract a few pixels
                                break;
                            }
                        }
                        else if (!isVertical)
                        {
                            // TrayNotifyWnd not found on horizontal: fallback to right alignment,
                            // since we are aligning to the right side and know the size of the taskbar.
                            primaryPos += taskbarWidth - physicalWidth - 20;
                            break;
                        }
                    }

                    // Final fallback: place near the end of the taskbar
                    primaryPos += primarySize - physicalWidth - 20;
                }
                catch (Exception ex)
                {
                    // Fallback to left alignment
                    Logger.Warn(ex, "Failed to get System Tray position.");
                    primaryPos = isVertical ? primarySize - physicalWidth - 20 : 20;
                }
                break;
        }

        primaryPos += SettingsManager.Current.TaskbarWidgetManualPadding;

        // Set widget position within canvas
        // primaryPos → left (horizontal) or top (vertical); crossPos → top (horizontal) or left (vertical)
        Canvas.SetLeft(Widget, (isVertical ? crossPos : primaryPos) / dpiScale);
        Canvas.SetTop(Widget, (isVertical ? primaryPos : crossPos) / dpiScale);
        Widget.Width = physicalWidth / dpiScale;
        Widget.Height = physicalHeight / dpiScale;

        // After 90° LayoutTransform the visual bounding rect has swapped dimensions
        double rectW = isVertical ? physicalHeight : physicalWidth;
        double rectH = isVertical ? physicalWidth : physicalHeight;
        return new Rect(Canvas.GetLeft(Widget) * dpiScale, Canvas.GetTop(Widget) * dpiScale, rectW, rectH);
    }

    private Rect PositionVisualizer(IntPtr taskbarHandle, RECT taskbarRect, double dpiScale, bool isMainTaskbarSelected, bool isVertical)
    {
        if (!SettingsManager.Current.TaskbarVisualizerEnabled)
            return Rect.Empty;

        // Rotate visualizer 90° on vertical taskbar so it fits the slim width
        TaskbarVisualizer.LayoutTransform = isVertical ? new System.Windows.Media.RotateTransform(90) : null;

        int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        int taskbarWidth = taskbarRect.Right - taskbarRect.Left;

        // TaskbarVisualizer.Height (40) is the cross-axis extent for both orientations:
        //   horizontal: actual height = 40, centered vertically (-1 to match native element alignment)
        //   vertical:   visual width after rotation = 40, centered horizontally
        int crossSize = isVertical ? taskbarWidth : taskbarHeight;
        int crossOffset = isVertical ? 0 : -1; // -1 aligns with native taskbar elements on horizontal
        int crossPos = (crossSize - (int)(TaskbarVisualizer.Height * dpiScale)) / 2 + crossOffset;

        // TaskbarVisualizer.Width (84) is the primary-axis extent for both orientations:
        //   horizontal: actual width = 84
        //   vertical:   visual height after rotation = 84
        // Position adjacent to the widget along the primary axis
        double widgetPrimaryStart = isVertical ? Canvas.GetTop(Widget) : Canvas.GetLeft(Widget);
        int primaryPos;

        switch (SettingsManager.Current.TaskbarVisualizerPosition)
        {
            case 0: // before widget (left for horizontal, above for vertical)
                primaryPos = (int)(widgetPrimaryStart * dpiScale) - (int)(TaskbarVisualizer.Width * dpiScale);
                break;

            case 1: // after widget (right for horizontal, below for vertical)
                // Widget.Width holds the logical width; after 90° rotation its visual height = Widget.Width * dpiScale
                primaryPos = (int)(widgetPrimaryStart * dpiScale) + (int)(Widget.Width * dpiScale);
                break;

            default:
                primaryPos = 0;
                break;
        }

        // Set visualizer position within canvas
        // primaryPos → left (horizontal) or top (vertical); crossPos → top (horizontal) or left (vertical)
        Canvas.SetLeft(TaskbarVisualizer, (isVertical ? crossPos : primaryPos) / dpiScale);
        Canvas.SetTop(TaskbarVisualizer, (isVertical ? primaryPos : crossPos) / dpiScale);

        // After 90° LayoutTransform the visual bounding rect has swapped dimensions
        double rectW = isVertical ? TaskbarVisualizer.Height * dpiScale : TaskbarVisualizer.Width * dpiScale;
        double rectH = isVertical ? TaskbarVisualizer.Width * dpiScale : TaskbarVisualizer.Height * dpiScale;
        return new Rect(Canvas.GetLeft(TaskbarVisualizer) * dpiScale, Canvas.GetTop(TaskbarVisualizer) * dpiScale, rectW, rectH);
    }

    /// <summary>
    /// Forwards a playback status to the widget without re-reading the media properties, so the play/pause
    /// icon can follow a play/pause command immediately instead of waiting for the session's playback state
    /// notification.
    /// </summary>
    public void SetPlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        Widget.SetPlaybackStatus(status);
    }

    public void UpdateUi(string title, string artist, BitmapImage? icon, GlobalSystemMediaTransportControlsSessionPlaybackStatus? playbackStatus, GlobalSystemMediaTransportControlsSessionPlaybackControls? playbackControls = null)
    {
        if (!SettingsManager.Current.TaskbarWidgetEnabled)
        {
            if (_timer.IsEnabled) // pause timer to save resources
                _timer.Stop();

            Dispatcher.Invoke(() =>
            {
                Visibility = Visibility.Collapsed;
            });
            return;
        }

        // Autohide - Widget hides when playback is paused
        _lastPlaybackStatus = playbackStatus;

        if ((SettingsManager.Current.TaskbarWidgetAutoHide))
        {
            if (playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                _autoHideTimer?.Stop();
                _autoHideTimer = null;

                Dispatcher.Invoke(() =>
                {
                    Visibility = Visibility.Visible;
                });
            }
            else
            {
                // Start delayed hide
                if (_autoHideTimer == null)
                {
                    var localTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(750)
                    };

                    localTimer.Tick += (s, e) =>
                    {
                        localTimer.Stop();
                        if (_autoHideTimer == localTimer)
                            _autoHideTimer = null;

                        if (_lastPlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                Visibility = Visibility.Collapsed;
                            });
                        }
                    };

                    _autoHideTimer = localTimer;
                    localTimer.Start();
                }
            }
        }

        if (!_timer.IsEnabled)
            _timer.Start();

        // Delegate UI update to widget control
        Widget.UpdateUi(title, artist, icon, playbackStatus, playbackControls);

        // Update position after UI change
        Dispatcher.BeginInvoke(() => UpdatePosition(), DispatcherPriority.Background);

        Dispatcher.Invoke(() =>
        {
            Visibility = Visibility.Visible;
        });
    }

    public void RefreshAppVolumeTooltip()
    {
        Widget.RefreshAppVolumeTooltip();
    }

    /// <summary>
    /// Looks up a taskbar element through UI Automation and returns its bounding rectangle.
    /// </summary>
    /// <remarks>
    /// The automation calls run on a worker thread because a taskbar provider that has stopped
    /// responding blocks whoever calls it. The await gives up after a fixed budget instead of blocking
    /// the UI thread for the whole query, which is what the previous Wait(1000)/Wait(500) calls did.
    /// The cached element is passed by value and returned, because async methods cannot use ref parameters.
    /// </remarks>
    /// <returns>The found flag, the rectangle, and the element to cache for the next call.</returns>
    private async Task<(bool Found, Rect Rect, AutomationElement? Element)> GetTaskbarXamlElementRectAsync(
        IntPtr taskbarHandle, AutomationElement? elementCache, string elementName)
    {
        if (taskbarHandle == IntPtr.Zero)
            return (false, Rect.Empty, elementCache);

        try
        {
            // reset if monitor changed
            if (_lastSelectedMonitor != SettingsManager.Current.TaskbarWidgetSelectedMonitor)
                elementCache = null;

            // find widget in XAML
            if (elementCache == null)
            {
                if (_pendingAutomationTasks.TryGetValue(elementName, out var pendingTask) && !pendingTask.IsCompleted)
                    return (false, Rect.Empty, elementCache);

                var findTask = Task.Run(() =>
                {
                    var root = AutomationElement.FromHandle(taskbarHandle);
                    return root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, elementName));
                });
                _pendingAutomationTasks[elementName] = findTask;

                try
                {
                    elementCache = await findTask.WaitAsync(AutomationFindTimeout);
                }
                catch (TimeoutException)
                {
                    Logger.Warn("Timeout querying taskbar XAML element: " + elementName);
                    return (false, Rect.Empty, elementCache);
                }
            }

            if (elementCache == null) // widget most likely disabled
                return (false, Rect.Empty, elementCache);

            try
            {
                if (_pendingAutomationTasks.TryGetValue(elementName, out var pendingTask) && !pendingTask.IsCompleted)
                {
                    return (false, Rect.Empty, null);
                }

                AutomationElement cachedElement = elementCache;
                Task<Rect> boundsTask = Task.Run(() => cachedElement.Current.BoundingRectangle);
                _pendingAutomationTasks[elementName] = boundsTask;

                Rect elementRect;
                try
                {
                    elementRect = await boundsTask.WaitAsync(AutomationBoundsTimeout);
                }
                catch (TimeoutException)
                {
                    Logger.Warn("Timeout getting bounds for taskbar XAML element: " + elementName);
                    return (false, Rect.Empty, null);
                }

                if (elementRect == Rect.Empty) // widget shown before but most likely disabled now
                {
                    return (false, Rect.Empty, null); // reset cache
                }

                return (true, elementRect, elementCache);
            }
            catch (ElementNotAvailableException)
            {
                // element became stale, reset cache
                Logger.Warn("Taskbar XAML element became stale, resetting cache: " + elementName);
                return (false, Rect.Empty, null);
            }
        }
        catch (COMException ex)
        {
            Logger.Warn(ex, "COM error retrieving taskbar XAML element Rect: " + elementName);
            return (false, Rect.Empty, null); // reset cache on error
        }
        catch (ElementNotAvailableException)
        {
            Logger.Warn("Taskbar XAML element not available, resetting cache: " + elementName);
            return (false, Rect.Empty, null);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error retrieving taskbar XAML element Rect: " + elementName);
            return (false, Rect.Empty, null); // reset cache on error
        }
    }

    /// <summary>
    /// Attempts to locate the Windows taskbar widgets button and retrieves its bounding rectangle.
    /// </summary>
    /// <returns>A tuple where the first value indicates whether the widgets button was found (<see langword="true"/> if found;
    /// otherwise, <see langword="false"/>), and the second value is the bounding rectangle of the button if found, or
    /// <see cref="Rect.Empty"/> if not found.</returns>
    private async Task<(bool, Rect)> GetTaskbarWidgetRectAsync(IntPtr taskbarHandle)
    {
        (bool found, Rect rect, AutomationElement? element) = await GetTaskbarXamlElementRectAsync(taskbarHandle, _widgetElement, "WidgetsButton");
        _widgetElement = element;
        return (found, rect);
    }

    private async Task<(bool, Rect)> GetSystemTrayRectAsync(IntPtr taskbarHandle)
    {
        (bool found, Rect rect, AutomationElement? element) = await GetTaskbarXamlElementRectAsync(taskbarHandle, _trayElement, "SystemTrayIcon");
        _trayElement = element;
        return (found, rect);
    }

    private async Task<(bool, Rect)> GetTaskbarFrameRectAsync(IntPtr taskbarHandle)
    {
        (bool found, Rect rect, AutomationElement? element) = await GetTaskbarXamlElementRectAsync(taskbarHandle, _taskbarFrameElement, "TaskbarFrame");
        _taskbarFrameElement = element;
        return (found, rect);
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        _timer.Stop();
        _autoHideTimer?.Stop();
        _autoHideTimer = null;
        _widgetElement = null;
        _trayElement = null;
        _taskbarFrameElement = null;
        _pendingAutomationTasks.Clear();
        base.OnClosed(e);
    }
}