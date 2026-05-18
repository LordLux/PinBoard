using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

    private readonly IHotkeyService    _hotkey;
    private readonly IWindowPositioner _positioner;
    private readonly ISettingsService  _settings;
    private readonly IPasteService     _paster;

    private AppWindow _appWindow = null!;
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

        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        _appWindow.SetPresenter(presenter);
        _appWindow.IsShownInSwitchers = false;

        SystemBackdrop = new DesktopAcrylicBackdrop();
        ApplyRoundedCorners();
        ApplyToolWindowStyle();

        // Off-screen at 1×1 so the first Activate() is invisible.
        _appWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));

        Activated += OnActivated;
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

    // ── Quick-paste 1–9 ──────────────────────────────────────────────────────

    // Fires when a key is pressed inside the popup but NOT consumed by a child
    // (e.g. typing in SearchBox marks keys as Handled, so they never reach here).
    private async void RootGrid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        var key = args.Key;
        if (key < VirtualKey.Number1 || key > VirtualKey.Number9) return;

        var idx = (int)key - (int)VirtualKey.Number1; // 0-based
        if (idx >= ViewModel.Items.Count) return;

        args.Handled = true;

        var shiftDown = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

        await ViewModel.PasteItemAsync(ViewModel.Items[idx], asText: shiftDown);
        if (!_isPinned) HidePopup();
    }
}
