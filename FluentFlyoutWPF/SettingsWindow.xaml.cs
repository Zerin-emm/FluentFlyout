// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Pages;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace FluentFlyoutWPF;

public partial class SettingsWindow : FluentWindow
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static SettingsWindow? instance;
    private Type? _currentPageType;
    private ScrollViewer? _contentScrollViewer;
    private List<SearchItem> _allSearchItems = [];
    private string? _pendingHighlightElementId = null;
    private System.ComponentModel.PropertyChangedEventHandler? _settingsPropertyChangedHandler;
    private Wpf.Ui.Appearance.ThemeChangedEvent? _applicationThemeChangedHandler;

    /// <summary>
    /// Generation counter for <see cref="HandleApplicationThemeChangedAsync"/>. A theme change is reported
    /// twice - once by the setting and once by WPF-UI - and only the newest repair may act.
    /// </summary>
    private int _themeRepairGeneration;
    static readonly Regex SplitCamelCaseRegex = new(@"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);

    public SettingsWindow()
    {
        if (instance != null)
        {
            if (instance.WindowState == WindowState.Minimized)
            {
                instance.WindowState = WindowState.Normal;
            }

            instance.Activate();
            instance.Focus();
            Close();
            return;
        }

        InitializeComponent();
        instance = this;

        Closed += (s, e) =>
        {
            // the settings object outlives this window, so a handler left behind would keep the whole
            // closed window (and its visual tree) alive and would keep running its navigation logic
            if (_settingsPropertyChangedHandler != null)
            {
                SettingsManager.Current.PropertyChanged -= _settingsPropertyChangedHandler;
                _settingsPropertyChangedHandler = null;
            }

            if (_applicationThemeChangedHandler != null)
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= _applicationThemeChangedHandler;
                _applicationThemeChangedHandler = null;
            }

            instance = null;
        };
        DataContext = SettingsManager.Current;

        // WPF-UI only re-applies the backdrop to Application.Current.MainWindow when the application theme
        // changes (ApplicationThemeManager.Apply), and this window is not the main window, so its Mica tint
        // would keep whatever theme was active when it was created - which is why the effect only came back
        // after the GUI was reopened. The theme event covers every route that can change the theme,
        // including a Windows theme change while AppTheme is "System" - and that route needs the navigation
        // repair as well, because it never reaches the settings property handler below and the template it
        // rebuilds leaves the window showing no page at all until the user navigates away and back.
        _applicationThemeChangedHandler = (_, _) => _ = HandleApplicationThemeChangedAsync();
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += _applicationThemeChangedHandler;

        RootNavigation.SetCurrentValue(NavigationView.IsPaneOpenProperty, false);
    }

    public static void ShowInstance(string? navigationPage = null)
    {
        if (instance == null)
        {
            new SettingsWindow().Show();
            instance?.Activate();
        }
        else
        {
            if (instance.WindowState == WindowState.Minimized)
            {
                instance.WindowState = WindowState.Normal;
            }

            instance.Activate();
            instance.Focus();
        }

        if (navigationPage != null)
        {
            var pageType = System.Reflection.Assembly
                .GetExecutingAssembly()
                .GetType($"FluentFlyoutWPF.Pages.{navigationPage}");
            if (pageType != null)
                NavigateToPage(pageType);
        }
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SearchItem selectedItem)
        {
            if (selectedItem.TargetPageType != null)
            {
                if (_currentPageType != selectedItem.TargetPageType)
                {
                    _pendingHighlightElementId = selectedItem.TargetElementId;
                    RootNavigation.Navigate(selectedItem.TargetPageType);
                }
                else if (!string.IsNullOrEmpty(selectedItem.TargetElementId))
                {
                    // Already on the page, just scroll and highlight
                    ScrollToAndHighlight(selectedItem.TargetElementId);
                }
            }
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var query = sender.Text.ToLowerInvariant();
            var matches = _allSearchItems.Where(x => x.Title.ToLowerInvariant().Contains(query)).ToList();
            sender.ItemsSource = matches;
            sender.IsSuggestionListOpen = matches.Count > 0;
        }
    }

    private void FluentWindow_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SearchBox.IsKeyboardFocusWithin && !SearchBox.IsMouseOver)
        {
            SearchBox.IsSuggestionListOpen = false;
            // Move focus to the window to unfocus the search box
            this.Focus();
        }
    }

    public static void NavigateToPage(Type pageType)
    {
        instance?.RootNavigation.Navigate(pageType);
    }

    private void BuildSearchItems()
    {
        var items = new List<SearchItem>();

        // Add all tabs
        foreach (var navItem in RootNavigation.MenuItems.OfType<NavigationViewItem>().Concat(RootNavigation.FooterMenuItems.OfType<NavigationViewItem>()))
        {
            if (navItem.Content != null)
            {
                items.Add(new SearchItem { Title = navItem.Content.ToString()!, TargetPageType = navItem.TargetPageType });
            }
        }

        // Add specific settings deep links from auto-generated static array
        foreach (var item in SearchItems)
        {
            string title = Application.Current.TryFindResource(item.ResourceKey)?.ToString() ?? item.ResourceKey;
            // Clean up the page type name (e.g. "SystemPage" -> "System") and split camel case (e.g. "MediaFlyout" -> "Media Flyout")
            string pageName = SplitCamelCaseRegex.Replace(item.TargetPageType.Name.Replace("Page", ""), " ");
            items.Add(new SearchItem { Title = $"{title}", Subtitle = pageName, TargetPageType = item.TargetPageType, TargetElementId = item.TargetElementId });
        }

        _allSearchItems = items;
        SearchBox.OriginalItemsSource = _allSearchItems;
    }

    private void ScrollToAndHighlight(string elementId)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var targetElement = FindChildByName<FrameworkElement>(RootNavigation, elementId);
                if (targetElement != null)
                {
                    targetElement.BringIntoView();

                    // Heartbeat animation
                    var heartbeatAnimation = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 1.0,
                        To = 0.5,
                        Duration = new Duration(TimeSpan.FromMilliseconds(300)),
                        AutoReverse = true,
                        RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2)
                    };
                    targetElement.BeginAnimation(UIElement.OpacityProperty, heartbeatAnimation);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error scrolling to and highlighting element");
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RootNavigation.IsPaneOpen = false;

        _currentPageType = typeof(HomePage);
        RootNavigation.Navigate(_currentPageType);

        // wrkaround for WPF-UI NavigationView theme change bug:
        // force pane initialization by toggling it once to prevent width corruption on theme changes
        // not sure why this has to be done
        await Task.Delay(100);
        RootNavigation.IsPaneOpen = true;
        await Task.Delay(10);
        RootNavigation.IsPaneOpen = false;

        RootNavigation.Navigated += (s, args) =>
        {
            _currentPageType = args.Page?.GetType();
            ResetScrollPosition();
            if (!string.IsNullOrEmpty(_pendingHighlightElementId))
            {
                var elementId = _pendingHighlightElementId;
                _pendingHighlightElementId = null;
                // Add a slight delay to ensure page is fully rendered before finding child and scrolling
                Task.Delay(300).ContinueWith(_ => ScrollToAndHighlight(elementId));
            }
        };

        _settingsPropertyChangedHandler = async (s, args) =>
        {
            if (args.PropertyName == nameof(SettingsManager.Current.AppTheme))
            {
                await HandleApplicationThemeChangedAsync();
            }
            else if (args.PropertyName == nameof(SettingsManager.Current.AppLanguage))
            {
                await Dispatcher.InvokeAsync(
                    () =>
                    {
                        BuildSearchItems();
                    },
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };

        SettingsManager.Current.PropertyChanged += _settingsPropertyChangedHandler;

        BuildSearchItems();
    }

    /// <summary>
    /// Brings the window back into a working state after the application theme changed. Called both when the
    /// theme setting changed and when WPF-UI reports a theme it applied on its own
    /// (<see cref="ApplicationThemeManager.Changed"/>). A Windows light/dark switch while the application
    /// follows the system theme is applied by <c>SystemThemeWatcher</c>, leaves AppTheme untouched and would
    /// otherwise never reach this window: the NavigationView is then left in the rebuilt-template state
    /// described below and, because that rebuild drops the page, the settings window shows an empty content
    /// area until the user navigates to another page and back by hand.
    /// <para>
    /// Only the newest request is carried out: a setting change applies the theme and reports it twice, and
    /// repairing twice would navigate the page twice for a single theme change.
    /// </para>
    /// </summary>
    private async Task HandleApplicationThemeChangedAsync()
    {
        if (!IsLoaded)
        {
            // Nothing to repair before the window has built its own template; it picks the theme up itself.
            return;
        }

        var wasPaneOpen = RootNavigation.IsPaneOpen;
        var openPaneLength = RootNavigation.OpenPaneLength;
        var generation = ++_themeRepairGeneration;

        // A theme change is applied through MicaWPF, whose refresh re-styles every element of the
        // window (FrameworkElementExtension.RefreshStyle). For the NavigationView that rebuilds its
        // ControlTemplate, and the rebuild can leave MicaWPF's throwaway Style on the control instead
        // of its own implicit style. NavigationView overrides the default style, so nothing applies
        // the implicit style again by itself: measured on the real window, the control is then left
        // with a null Template, a Style whose target type is the sealed IFrameworkInputElement,
        // CompactPaneLength 0 and the template root of the detached tree, whose two columns both fell
        // back to "*". The pane is left in half the window and centred inside it (measured 96 px to
        // the right while open and 201 px while collapsed, with the page pushed away by the same
        // distance), its pane states belong to a template root that is no longer in the tree, and the
        // next IsPaneOpen change makes WPF's state machine generate transition animations for that
        // dead name scope - the NullReferenceException from TemplateNameScope.FindName, which used to
        // escape to the global handler, kill the process a few seconds after the second click on the
        // pane toggle button and, by aborting the assignments after it, leave IsPaneOpen and
        // OpenPaneLength behind in the wrong state and the pane toggle button dead.
        // The repair therefore has to run after that refresh - and it has to run before the dispatcher
        // renders the turn the refresh runs in, or a frame of the broken layout is painted first.
        // DataBind runs after every Normal-priority item (the refresh is queued at Normal) and before
        // Render, which is exactly that slot.
        if (!wasPaneOpen)
        {
            // A rebuilt template lays its pane out at OpenPaneLength, so building the collapsed pane
            // from the open length would sweep it down over a hardcoded 160 ms afterwards
            // (NavigationViewCompact.xaml: PaneCompact animates PaneGrid.Width from
            // "{TemplateBinding OpenPaneLength}" to 40) - the flash of the sidebar opening and
            // closing again the user saw on every theme change. Collapsing the open length for the
            // duration of the rebuild makes the pane build at the compact width instead; the real
            // length comes back once the rebuilt pane has been put into its compact state.
            RootNavigation.OpenPaneLength = CompactPaneWidth;
        }

        await Dispatcher.InvokeAsync(
            () =>
            {
                if (generation != _themeRepairGeneration)
                {
                    return;
                }

                RepairNavigationViewAfterThemeChange(wasPaneOpen);
                RefreshWindowBackdrop();
            },
            System.Windows.Threading.DispatcherPriority.DataBind);

        await Dispatcher.InvokeAsync(
            () =>
            {
                if (generation != _themeRepairGeneration)
                {
                    return;
                }

                // Second look: the refresh re-styles every element of the window, including the ones
                // the rebuilt template just created, so a throwaway style can still land after the
                // repair above, and the state machine only finds the rebuilt template's panes once
                // the layout pass has put them in the tree - which is after the DataBind turn. That is
                // why the pane state is pushed here as well. Restoring the open length afterwards is
                // safe: OpenPaneLength has no change callback in WPF-UI, and the compact width is
                // pinned by the repair (see RepairNavigationViewAfterThemeChange) rather than left to
                // the state's own width animation.
                RepairNavigationViewAfterThemeChange(wasPaneOpen);
                RefreshWindowBackdrop();
                if (!wasPaneOpen)
                {
                    RootNavigation.OpenPaneLength = openPaneLength;

                    // The pane was collapsed when the theme changed, so it was rebuilt at the
                    // compact length and pinned there. If the user opens it before the real length
                    // comes back, the state's own width animation resolves its target from the
                    // compact length and would hold an open pane at 40 px. Re-applying the open
                    // state now that the length is restored fixes that; while the pane is still
                    // collapsed - the normal case - this costs nothing.
                    if (RootNavigation.IsPaneOpen)
                    {
                        RepairNavigationViewAfterThemeChange(true);
                    }
                }
            },
            System.Windows.Threading.DispatcherPriority.Loaded);

        // The rebuilt template comes with a new frame, so the page the user was looking at is no
        // longer in the visual tree and the scroll viewer it used to hand out is detached: drop
        // the cached one so the next ResetScrollPosition() picks up the live viewer.
        _contentScrollViewer = null;

        await Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(300);
            if (generation != _themeRepairGeneration)
            {
                return;
            }

            // Re-navigating is what makes the FluentWindow re-render its chrome under the new theme,
            // but it has to go back to the page the user is already on - this used to always navigate
            // to HomePage and throw the user out of the open page. See NavigateToCurrentPage for why
            // the trip has to bounce through another page.
            NavigateToCurrentPage();
            BuildSearchItems();

            // A theme change can also arrive late - the system theme is reported by MicaWPF's own
            // watcher as well, which may run after this pass - and rebuild the template a second time,
            // taking the page with it. Checking whether the page is still attached is what makes this
            // repair hold for the system-driven route too, whatever order the theme services run in.
            await Task.Delay(400);
            if (generation != _themeRepairGeneration || IsCurrentPageAttached())
            {
                return;
            }

            RepairNavigationViewAfterThemeChange(wasPaneOpen);
            RefreshWindowBackdrop();
            NavigateToCurrentPage();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Whether the page the navigation view is showing is still attached to the frame that hosts it. A theme
    /// change can rebuild the NavigationView template, and the rebuilt template starts out with an empty frame
    /// while the navigation view keeps believing that its page is current.
    /// </summary>
    private bool IsCurrentPageAttached() => FindVisualChild<Frame>(RootNavigation)?.Content != null;

    /// <summary>
    /// Navigates back to the page the user is looking at. WPF-UI treats "navigate to the page type that is
    /// already current" as a no-op, which leaves a freshly built, still empty frame showing nothing, so the
    /// trip bounces through another page to force a real navigation.
    /// </summary>
    private void NavigateToCurrentPage()
    {
        var targetPage = _currentPageType ?? typeof(HomePage);
        RootNavigation.Navigate(targetPage == typeof(HomePage) ? typeof(SystemPage) : typeof(HomePage));
        RootNavigation.Navigate(targetPage);
    }

    /// <summary>
    /// The width the collapsed pane is built at. NavigationViewCompact.xaml animates PaneGrid.Width to this
    /// literal and WPF-UI's own CompactPaneLength reads as 0 in the version this app uses.
    /// </summary>
    private const double CompactPaneWidth = 40;

    /// <summary>
    /// Brings the NavigationView back into a working state after a theme change. See the long comment in the
    /// AppTheme handler of <see cref="SettingsWindow_Loaded"/>: the template rebuild MicaWPF's style refresh
    /// causes can leave the control with that refresh's throwaway Style instead of its implicit style, and
    /// with it a detached template root whose columns both fall back to "*" (the pane and the page are then
    /// moved 96 px / 201 px to the right) and a name scope the next pane state change throws on. Clearing the
    /// throwaway style makes WPF resolve the implicit style again and build a healthy template.
    /// </summary>
    private void RepairNavigationViewAfterThemeChange(bool wasPaneOpen)
    {
        try
        {
            var style = RootNavigation.Style;

            // The throwaway Style MicaWPF assigns has no target type of its own; WPF seals such a style as
            // IFrameworkInputElement, which is what tells it apart from the real implicit style.
            var hasThrowawayStyle = style != null && style.TargetType == typeof(IFrameworkInputElement);

            if (RootNavigation.Template == null || hasThrowawayStyle)
            {
                RootNavigation.ClearValue(FrameworkElement.StyleProperty);
            }

            if (RootNavigation.Template == null)
            {
                RootNavigation.ApplyTemplate();
            }

            if (RootNavigation.Template == null)
            {
                // Clearing the style above makes WPF resolve the implicit style again, but the template it
                // carries is only built once a layout pass runs, and the pane state machine can only find the
                // panes of a template that is in the tree. The first repair runs before the dispatcher renders
                // the turn the theme change ran in, so running the layout here is what lets the assignments
                // below reach the rebuilt pane in time for that frame instead of the one after it.
                RootNavigation.UpdateLayout();
            }

            if (RootNavigation.Template == null)
            {
                return;
            }

            // The pane state has to be pushed onto the rebuilt template: WPF-UI only runs its state machine
            // when IsPaneOpen changes, and the rebuilt template starts out in the arrangement its XAML
            // declares. The auto-suggest row sits above the item list with a 6 px bottom margin and is
            // collapsed while this app keeps AutoSuggestBox null, while both pane states set it to Visible or
            // Hidden and therefore add those 6 px back: without the assignment below the rebuilt pane is
            // drawn 6 px higher for the 160 ms the compact state waits before hiding the row (its Visibility
            // key frame carries BeginTime 0:0:0.16), which is the pane jumping up and dropping back down the
            // user sees on every theme change. Setting it by hand first also makes it the value the state
            // animation uses as its implicit starting value, so the very first frame after the rebuild
            // already shows the arrangement the pane settles on. Transitions are passed as false because
            // generating them for the rebuilt tree is exactly what throws.
            if (RootNavigation.Template.FindName("AutoSuggestBoxContentPresenter", RootNavigation) is FrameworkElement autoSuggestRow)
            {
                autoSuggestRow.Visibility = wasPaneOpen ? Visibility.Visible : Visibility.Hidden;
            }

            VisualStateManager.GoToState(RootNavigation, wasPaneOpen ? "PaneOpen" : "PaneCompact", false);

            if (RootNavigation.Template.FindName("PaneGrid", RootNavigation) is FrameworkElement paneGrid)
            {
                // Both pane states animate PaneGrid.Width: PaneCompact from "{TemplateBinding OpenPaneLength}"
                // down to the literal 40, PaneOpen from that literal back up to OpenPaneLength. The rebuilt
                // template already lays the pane out at the right width, and the state animation then
                // overrides it a render later - with whatever the open length happens to be by then. That is
                // what swept the collapsed pane down from the open length, and what made an open pane slide
                // out of 40 px, on every theme change. Pinning the width the rebuild produced keeps still in
                // both cases. The pin is the oldest animation on the property, so the next real pane state
                // change overrides it, and a rebuilt template brings a new pane grid with it.
                paneGrid.BeginAnimation(
                    FrameworkElement.WidthProperty,
                    new System.Windows.Media.Animation.DoubleAnimation
                    {
                        To = wasPaneOpen ? RootNavigation.OpenPaneLength : CompactPaneWidth,
                        Duration = TimeSpan.Zero,
                        FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd,
                    }
                );
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to repair the navigation view after a theme change");
        }
    }

    /// <summary>
    /// Re-applies the window backdrop after a theme change. WPF-UI's <c>ApplicationThemeManager.Apply</c>
    /// refreshes the backdrop of <c>Application.Current.MainWindow</c> only, and in this app the main window is
    /// the flyout window (App.xaml opens MainWindow.xaml before the settings window is ever created), so the
    /// settings window stays on the DWM attributes of the theme it was created under - including the immersive
    /// dark mode flag that picks the Mica sheet's tint. Measured with the app in the dark theme: a window
    /// created at that moment reports an immersive dark mode of 1 while this window still reports 0, which is
    /// why the backdrop used to look washed out and flat until the GUI was closed and reopened. Doing what a
    /// fresh window does in FluentWindow.OnBackdropTypeChanged restores it: the client area is made transparent
    /// again so the backdrop is not covered, and the backdrop is applied against the current app theme.
    /// </summary>
    private void RefreshWindowBackdrop()
    {
        try
        {
            WindowBackdrop.RemoveBackground(this);
            WindowBackdrop.ApplyBackdrop(this, WindowBackdropType);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to refresh the window backdrop after a theme change");
        }
    }

    private void SettingsWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SettingsManager.SaveSettings();
    }

    private void ResetScrollPosition()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _contentScrollViewer ??= FindScrollableScrollViewer(RootNavigation);

                if (_contentScrollViewer != null)
                {
                    _contentScrollViewer.ScrollToVerticalOffset(0);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error resetting scroll position in SettingsWindow");
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // helper functions to traverse visual tree

    private static T? FindChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild && typedChild.Name == name)
            {
                return typedChild;
            }

            var result = FindChildByName<T>(child, name);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }

    private static ScrollViewer? FindScrollableScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv && sv.ScrollableHeight > 0)
            {
                return sv;
            }

            var result = FindScrollableScrollViewer(child);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var result = FindVisualChild<T>(child);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }

    public class SearchItem
    {
        public string Title { get; set; } = string.Empty;
        public string? Subtitle { get; set; }
        public Type? TargetPageType { get; set; }
        public string? TargetElementId { get; set; }
        public override string ToString() => Title;
    }
}