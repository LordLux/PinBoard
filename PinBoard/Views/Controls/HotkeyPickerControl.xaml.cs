using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PinBoard.Views.Controls;

/// Hotkey picker: modifier checkboxes (Win/Ctrl/Alt/Shift) + key dropdown + Reset button.
/// Modifiers and HotkeyKey are uint DependencyProperties matching RegisterHotKey bit layout:
///   MOD_ALT=1, MOD_CTRL=2, MOD_SHIFT=4, MOD_WIN=8.
/// No keyboard capture — entirely pointer/dropdown driven, so global hotkeys cannot interfere.
public sealed partial class HotkeyPickerControl : UserControl
{
    private const uint MOD_ALT   = 0x0001;
    private const uint MOD_CTRL  = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN   = 0x0008;

    private const uint DefaultModifiers = MOD_WIN | MOD_SHIFT; // Win+Shift
    private const uint DefaultKey       = 0x56u;               // V

    // ── Dependency properties ─────────────────────────────────────────────────

    public static readonly DependencyProperty ModifiersProperty =
        DependencyProperty.Register(nameof(Modifiers), typeof(uint), typeof(HotkeyPickerControl),
            new PropertyMetadata(DefaultModifiers, (d, _) => ((HotkeyPickerControl)d).SyncFromDeps()));

    public static readonly DependencyProperty HotkeyKeyProperty =
        DependencyProperty.Register(nameof(HotkeyKey), typeof(uint), typeof(HotkeyPickerControl),
            new PropertyMetadata(DefaultKey, (d, _) => ((HotkeyPickerControl)d).SyncFromDeps()));

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

    // ── Key list item ─────────────────────────────────────────────────────────

    private sealed record KeyItem(string Label, uint Vk)
    {
        public override string ToString() => Label;
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private bool _syncing; // prevent re-entrant update loops

    public HotkeyPickerControl()
    {
        InitializeComponent();
        PopulateKeyCombo();
        SyncFromDeps();
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    private void PopulateKeyCombo()
    {
        // Letters A–Z
        for (char c = 'A'; c <= 'Z'; c++)
            KeyCombo.Items.Add(new KeyItem(c.ToString(), (uint)c));

        // Digits 0–9
        for (int i = 0; i <= 9; i++)
            KeyCombo.Items.Add(new KeyItem(i.ToString(), (uint)('0' + i)));

        // Function keys F1–F12
        for (int i = 1; i <= 12; i++)
            KeyCombo.Items.Add(new KeyItem($"F{i}", (uint)(111 + i)));
    }

    // ── Sync: DPs → controls ──────────────────────────────────────────────────

    private void SyncFromDeps()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            WinCheck.IsChecked   = (Modifiers & MOD_WIN)   != 0;
            CtrlCheck.IsChecked  = (Modifiers & MOD_CTRL)  != 0;
            AltCheck.IsChecked   = (Modifiers & MOD_ALT)   != 0;
            ShiftCheck.IsChecked = (Modifiers & MOD_SHIFT) != 0;

            // Select the matching key in the combo.
            KeyCombo.SelectedItem = KeyCombo.Items
                .OfType<KeyItem>()
                .FirstOrDefault(k => k.Vk == HotkeyKey);

            UpdateWinVWarning();
        }
        finally
        {
            _syncing = false;
        }
    }

    // ── Sync: controls → DPs ──────────────────────────────────────────────────

    private void OnModifierChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;

        uint mods = 0;
        if (WinCheck.IsChecked   == true) mods |= MOD_WIN;
        if (CtrlCheck.IsChecked  == true) mods |= MOD_CTRL;
        if (AltCheck.IsChecked   == true) mods |= MOD_ALT;
        if (ShiftCheck.IsChecked == true) mods |= MOD_SHIFT;

        Modifiers = mods;
        UpdateWinVWarning();
    }

    private void OnKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (KeyCombo.SelectedItem is KeyItem ki)
        {
            HotkeyKey = ki.Vk;
            UpdateWinVWarning();
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        Modifiers  = DefaultModifiers;
        HotkeyKey  = DefaultKey;
        // SyncFromDeps fires automatically via DP callbacks.
    }

    // ── Warning ───────────────────────────────────────────────────────────────

    private void UpdateWinVWarning()
    {
        bool isWinV = Modifiers == MOD_WIN && HotkeyKey == 0x56u;
        WinVWarning.Visibility = isWinV ? Visibility.Visible : Visibility.Collapsed;
    }
}
