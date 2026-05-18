using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace PinBoard.Views.Controls;

/// Hotkey capture control. Shows the current combo; click "Set" then press any
/// non-modifier key combination to rebind.
/// Modifiers and Key are uint dependency properties that match RegisterHotKey bit layout:
///   MOD_ALT=1, MOD_CTRL=2, MOD_SHIFT=4, MOD_WIN=8.
public sealed partial class HotkeyCaptureBox : UserControl
{
    // ── Dependency properties ─────────────────────────────────────────────────

    public static readonly DependencyProperty ModifiersProperty =
        DependencyProperty.Register(nameof(Modifiers), typeof(uint), typeof(HotkeyCaptureBox),
            new PropertyMetadata(0u, (d, _) => ((HotkeyCaptureBox)d).RefreshDisplay()));

    public static readonly DependencyProperty HotkeyKeyProperty =
        DependencyProperty.Register(nameof(HotkeyKey), typeof(uint), typeof(HotkeyCaptureBox),
            new PropertyMetadata(0u, (d, _) => ((HotkeyCaptureBox)d).RefreshDisplay()));

    public uint Modifiers
    {
        get => (uint)GetValue(ModifiersProperty);
        set => SetValue(ModifiersProperty, value);
    }

    public uint HotkeyKey
    {
        get => (uint)GetValue(HotkeyKeyProperty);
        set => SetValue(HotkeyKeyProperty, value);
    }

    private bool _capturing;

    public HotkeyCaptureBox()
    {
        InitializeComponent();
        RefreshDisplay();
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void SetButton_Click(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        DisplayBox.Text = "Press a key combination…";
        DisplayBox.Focus(FocusState.Programmatic);
    }

    private void DisplayBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing) return;

        var key = e.Key;

        if (key == VirtualKey.Escape)
        {
            _capturing = false;
            RefreshDisplay();
            e.Handled = true;
            return;
        }

        // Ignore bare modifier keys — wait for the actual trigger key.
        if (key is VirtualKey.LeftWindows or VirtualKey.RightWindows
                or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
                or VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
                or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu)
            return;

        // Require at least one modifier.
        uint mods = GetHeldModifiers();
        if (mods == 0) return;

        _capturing = false;
        Modifiers  = mods;
        HotkeyKey  = (uint)key;

        e.Handled = true;
        RefreshDisplay();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RefreshDisplay()
    {
        if (!_capturing)
            DisplayBox.Text = FormatHotkey(Modifiers, HotkeyKey);
    }

    private static uint GetHeldModifiers()
    {
        uint m = 0;
        if (IsHeld(VirtualKey.LeftWindows)  || IsHeld(VirtualKey.RightWindows))  m |= 0x0008;
        if (IsHeld(VirtualKey.Menu)         || IsHeld(VirtualKey.LeftMenu)  || IsHeld(VirtualKey.RightMenu))  m |= 0x0001;
        if (IsHeld(VirtualKey.Control)      || IsHeld(VirtualKey.LeftControl) || IsHeld(VirtualKey.RightControl)) m |= 0x0002;
        if (IsHeld(VirtualKey.Shift)        || IsHeld(VirtualKey.LeftShift) || IsHeld(VirtualKey.RightShift)) m |= 0x0004;
        return m;
    }

    private static bool IsHeld(VirtualKey key) =>
        InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(CoreVirtualKeyStates.Down);

    private static string FormatHotkey(uint mods, uint key)
    {
        if (key == 0) return "(none)";
        var parts = new List<string>();
        if ((mods & 0x0008) != 0) parts.Add("Win");
        if ((mods & 0x0001) != 0) parts.Add("Alt");
        if ((mods & 0x0002) != 0) parts.Add("Ctrl");
        if ((mods & 0x0004) != 0) parts.Add("Shift");
        parts.Add(((char)key).ToString().ToUpperInvariant());
        return string.Join(" + ", parts);
    }
}
