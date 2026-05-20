using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PinBoard.Interop;
using PinBoard.Services;
using PinBoard.ViewModels;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;

namespace PinBoard.Views;

public sealed partial class ClipboardPopup : Window
{
    private const int PopupWidth  = 400;
    private const int PopupHeight = 520;

    private const uint WM_NCCALCSIZE                  = 0x0083;
    private const uint WM_SETCURSOR                   = 0x0020;
    private const uint WM_ENTERSIZEMOVE               = 0x0231;
    private const uint WM_EXITSIZEMOVE                = 0x0232;
    private const int  HTCAPTION                      = 2;
    private const int  DWMWA_USE_IMMERSIVE_DARK_MODE  = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    private const uint IMAGE_CURSOR    = 2;
    private const uint LR_LOADFROMFILE = 0x0010;

    // Custom cursors loaded from Assets/. Loaded lazily on first WM_SETCURSOR
    // hit so the cost isn't paid up-front. _isDragging tracks the OS modal
    // move loop so we can switch from Grab to Grabbing while the user is
    // actively dragging the window.
    private static IntPtr _grabCursor     = IntPtr.Zero;
    private static IntPtr _grabbingCursor = IntPtr.Zero;
    private static bool   _isDragging;

    private static IntPtr GetCursor(bool dragging)
    {
        if (dragging)
        {
            if (_grabbingCursor == IntPtr.Zero)
                _grabbingCursor = LoadCursorFile("Grabbing.cur");
            return _grabbingCursor;
        }
        if (_grabCursor == IntPtr.Zero)
            _grabCursor = LoadCursorFile("Grab.cur");
        return _grabCursor;
    }

    // cx/cy = 0 + no LR_DEFAULTSIZE → LoadImage returns the cursor at the
    // exact pixel size stored in the file. Size is whatever the .cur was
    // authored at; the application doesn't try to scale it.
    private static IntPtr LoadCursorFile(string name)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", name);
        return LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, 0, 0, LR_LOADFROMFILE);
    }

    private readonly IHotkeyService    _hotkey;
    private readonly IWindowPositioner _positioner;
    private readonly ISettingsService  _settings;
    private readonly IPasteService     _paster;

    private AppWindow                    _appWindow = null!;
    private WindowSubclass?              _subclass;
    private InputNonClientPointerSource? _ncInput;
    private bool      _isPinned;
    private bool      _firstActivation = true;

    private const int HotkeyId = 1;

    public ClipboardPopupViewModel ViewModel { get; }
    public nint Hwnd { get; private set; }

    public ClipboardPopup(ClipboardPopupViewModel viewModel,
                          IHotkeyService hotkey,
                          IWindowPositioner positioner,
                          ISettingsService settings,
                          IPasteService paster)
    {
        // ViewModel must be set BEFORE InitializeComponent so x:Bind compiles correctly.
        ViewModel  = viewModel;
        _hotkey    = hotkey;
        _positioner = positioner;
        _settings  = settings;
        _paster    = paster;

        InitializeComponent();
        ConfigureWindow();
        RegisterHotkey();

        // Restore persisted pin-window state.
        _isPinned = settings.IsPinned;
        PinWindowButton.IsChecked = _isPinned;
    }

    // ── Window configuration ─────────────────────────────────────────────────

    private void ConfigureWindow()
    {
        Hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(Hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // Borderless presenter + dark immersive frame: matches SettingsWindow,
        // avoids the black flash on first paint.
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.IsResizable    = false;
        presenter.IsMaximizable  = false;
        presenter.IsMinimizable  = false;
        presenter.IsAlwaysOnTop  = true;
        _appWindow.SetPresenter(presenter);
        _appWindow.IsShownInSwitchers = false;

        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "PinBoardLogo.ico");
        if (System.IO.File.Exists(iconPath)) _appWindow.SetIcon(iconPath);

        int useDark = 1;
        DwmSetWindowAttribute(Hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

        _subclass = new WindowSubclass(Hwnd, subclassId: 1, OnMessage);

        PInvoke.SetWindowPos(
            (HWND)Hwnd, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);

        _ncInput = InputNonClientPointerSource.GetForWindowId(windowId);

        // DragHandle has fixed Width, so its own SizeChanged never fires when
        // the window resizes from 1×1 to the real popup size — only its X
        // position changes (it's centred). Re-register on the root instead.
        RootGrid.SizeChanged += (_, _) => UpdateDragHandleRegion();

        ApplyTransparency(_settings.UseTransparency);
        ApplyRoundedCorners();
        ApplyToolWindowStyle();

        // Off-screen at 1×1 so the first Activate() is invisible.
        _appWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));

        Activated += OnActivated;
        Closed    += (_, _) => { _subclass?.Dispose(); _subclass = null; };
    }

    private static nint? OnMessage(uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_NCCALCSIZE:
                // Collapse non-client area to zero — kills the white/light
                // frame strip and gives WinUI 3 full client-area control.
                if (wParam != 0) return 0;
                break;

            case WM_SETCURSOR:
            {
                // ProtectedCursor doesn't apply over Caption regions (the OS
                // treats them as non-client). Lparam LOWORD = hit-test result;
                // return 1 to keep our cursor instead of the default arrow.
                int hit = (int)(lParam & 0xFFFF);
                if (hit == HTCAPTION)
                {
                    SetCursor(GetCursor(_isDragging));
                    return 1;
                }
                break;
            }

            case WM_ENTERSIZEMOVE:
                _isDragging = true;
                SetCursor(GetCursor(dragging: true));
                break;

            case WM_EXITSIZEMOVE:
                _isDragging = false;
                break;
        }
        return null;
    }

    private void DragHandle_Loaded(object sender, RoutedEventArgs e) =>
        UpdateDragHandleRegion();

    private void DragHandle_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateDragHandleRegion();

    // Register the tiny drag-handle pill as a Caption region with the OS.
    // The hit area is padded a bit larger than the visible pill so it's
    // easier to grab. All coords in physical pixels.
    private void UpdateDragHandleRegion()
    {
        if (_ncInput is null || DragHandle.XamlRoot is null) return;

        double scale = DragHandle.XamlRoot.RasterizationScale;
        var bounds = DragHandle.TransformToVisual(null)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, DragHandle.ActualWidth, DragHandle.ActualHeight));

        const double padX = 16;
        const double padY = 8;
        var rect = new RectInt32(
            _X:      (int)System.Math.Round((bounds.X - padX) * scale),
            _Y:      (int)System.Math.Round((bounds.Y - padY) * scale),
            _Width:  (int)System.Math.Round((bounds.Width  + padX * 2) * scale),
            _Height: (int)System.Math.Round((bounds.Height + padY * 2) * scale));

        _ncInput.SetRegionRects(NonClientRegionKind.Caption, new[] { rect });
    }

    private void ApplyRoundedCorners()
    {
        var pref = (uint)DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        unsafe
        {
            PInvoke.DwmSetWindowAttribute(
                (HWND)Hwnd,
                DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE,
                &pref, sizeof(uint));
        }
    }

    // ── Transparency toggle ──────────────────────────────────────────────
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SolidPopupBg
        = new(Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E));

    public void ApplyTransparency(bool useTransparency)
    {
        SystemBackdrop = useTransparency ? new DesktopAcrylicBackdrop() : null;

        if (useTransparency)
            RootGrid.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        else
            RootGrid.Background = SolidPopupBg;
    }

    private void ApplyToolWindowStyle()
    {
        const int GWL_EXSTYLE      = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        int ex = PInvoke.GetWindowLong((HWND)Hwnd, (WINDOW_LONG_PTR_INDEX)GWL_EXSTYLE);
        PInvoke.SetWindowLong((HWND)Hwnd, (WINDOW_LONG_PTR_INDEX)GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
    }

    private void RegisterHotkey()
    {
        _hotkey.HotkeyPressed += (_, _) => DispatcherQueue.TryEnqueue(TogglePopup);
        _hotkey.Register(Hwnd, HotkeyId, _settings.HotkeyModifiers, _settings.HotkeyKey);
    }

    // ── Show / hide ──────────────────────────────────────────────────────────

    public void TogglePopup()
    {
        if (_appWindow.IsVisible) HidePopup();
        else                      _ = ShowPopupAsync();
    }

    public async Task ShowPopupAsync()
    {
        // Stash before the popup steals focus.
        _paster.StashForegroundWindow();

        // Apply the "Default open group" setting BEFORE the refresh so the
        // first list query uses the right filter. Updates the tab highlight
        // too if it changed.
        ViewModel.ApplyDefaultGroupOnOpen();
        ApplyTabSelection(ViewModel.SelectedGroup);

        var pos = _positioner.GetPopupPosition(new SizeInt32(PopupWidth, PopupHeight));
        _appWindow.MoveAndResize(new RectInt32(pos.X, pos.Y, PopupWidth, PopupHeight));
        _appWindow.Show(activateWindow: true);
        PInvoke.SetForegroundWindow((HWND)Hwnd);

        await ViewModel.RefreshAsync();
        SearchBox.Focus(FocusState.Programmatic);
    }

    // Non-async overload for callers that can't await.
    public void ShowPopup() => _ = ShowPopupAsync();

    public void HidePopup()
    {
        _isPinned = false;
        PinWindowButton.IsChecked = false;
        SearchBox.Text = string.Empty;
        _appWindow.Hide();
    }

    private void OnActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (_firstActivation)
        {
            _firstActivation = false;
            _appWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));
            return;
        }

        if (args.WindowActivationState == WindowActivationState.Deactivated && !_isPinned)
            HidePopup();
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnEscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        HidePopup();
        args.Handled = true;
    }

    private async void ItemsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ClipItemViewModel vm) return;

        var shiftDown = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

        await ViewModel.PasteItemAsync(vm, asText: shiftDown);

        if (!_isPinned) HidePopup();
    }

    private void PinWindowButton_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = PinWindowButton.IsChecked == true;
        _settings.IsPinned = _isPinned;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        App.Current?.ShowSettingsWindow();

    // ── Filter tabs ──────────────────────────────────────────────────────────

    // Hover-switch timer: when the user hovers a tab and the matching setting
    // is on, we wait ~250ms before switching to that group. Lets the user
    // sweep past tabs without thrashing the underlying list query.
    private const int HoverSwitchDelayMs = 250;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hoverTimer;
    private string? _pendingHoverGroup;

    private void NavTab_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        var tag = el.Tag as string ?? "all";
        SelectTab(tag);
    }

    private void NavTab_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_settings.HoverSwitchGroup) return;
        if (sender is not FrameworkElement el) return;
        var tag = el.Tag as string ?? "all";
        if (tag == ViewModel.SelectedGroup) return;

        _pendingHoverGroup = tag;
        if (_hoverTimer is null) _hoverTimer = DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(HoverSwitchDelayMs);
        _hoverTimer.IsRepeating = false;
        _hoverTimer.Stop();
        _hoverTimer.Tick -= OnHoverTimerTick;
        _hoverTimer.Tick += OnHoverTimerTick;
        _hoverTimer.Start();
    }

    private void NavTab_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoverTimer?.Stop();
        _pendingHoverGroup = null;
    }

    private void OnHoverTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_pendingHoverGroup is { } tag) SelectTab(tag);
        _pendingHoverGroup = null;
    }

    private void SelectTab(string tag)
    {
        if (ViewModel.SelectedGroup == tag) return;
        ViewModel.SelectedGroup = tag;   // triggers VM refresh
        ApplyTabSelection(tag);
    }

    // Mirrors ApplyNavSelection in SettingsWindow: each tab's background
    // Border opacity is set explicitly so selection survives every state
    // transition (RadioButton-style VisualStates are unreliable in WinUI 3).
    private void ApplyTabSelection(string tag)
    {
        SetTabSelected(TabAllBg,     tag == "all");
        SetTabSelected(TabTextBg,    tag == "text");
        SetTabSelected(TabImageBg,   tag == "image");
        SetTabSelected(TabFileBg,    tag == "files");
        SetTabSelected(TabCollectBg, tag == "collect");
    }

    private static void SetTabSelected(FrameworkElement bg, bool selected) =>
        bg.Opacity = selected ? 1.0 : 0.35;

    // Apply outer-corner rounding so the hover highlight aligns with the
    // popup's rounded overlay: first item rounds the top corners, last item
    // rounds the bottom corners, and a single item rounds all four.
    private void ItemActionsFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;

        // Inner radius = outer overlay radius (8) − presenter padding (4) so
        // the item's rounded highlight sits flush inside the popup's rounded edge.
        const double r = 4;
        var items = flyout.Items;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not MenuFlyoutItem item) continue;

            bool first = i == 0;
            bool last  = i == items.Count - 1;
            // CornerRadius(topLeft, topRight, bottomRight, bottomLeft)
            item.CornerRadius = new CornerRadius(
                first ? r : 0,
                first ? r : 0,
                last  ? r : 0,
                last  ? r : 0);
        }
    }

    // ── Quick-paste 1–9 ──────────────────────────────────────────────────────

    // Fires when a key is pressed inside the popup but NOT consumed by a child
    // (e.g. typing in SearchBox marks keys as Handled, so they never reach here).
    private async void RootGrid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        var key = args.Key;

        // Ctrl+, → open Settings (popup must be focused for KeyDown to fire here).
        if (key == (VirtualKey)0xBC /* OemComma */)
        {
            var ctrlDown = Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(CoreVirtualKeyStates.Down);
            if (ctrlDown)
            {
                args.Handled = true;
                App.Current?.ShowSettingsWindow();
                return;
            }
        }

        if (key < VirtualKey.Number1 || key > VirtualKey.Number9) return;

        var idx = (int)key - (int)VirtualKey.Number1;
        if (idx >= ViewModel.Items.Count) return;

        args.Handled = true;

        var shiftDown = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

        await ViewModel.PasteItemAsync(ViewModel.Items[idx], asText: shiftDown);
        if (!_isPinned) HidePopup();
    }
}
