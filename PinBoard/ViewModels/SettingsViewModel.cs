using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PinBoard.Services;
using Windows.ApplicationModel;

namespace PinBoard.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IHistoryStore    _store;
    private readonly IHotkeyService   _hotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryCapDisplay))]
    private int _historyCap;

    [ObservableProperty] private bool _runAtStartup;
    [ObservableProperty] private bool _historyJustCleared;
    [ObservableProperty] private bool _useTransparency;
    [ObservableProperty] private bool _hoverSwitchGroup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TtlDescription))]
    private int _ttlDays;

    [ObservableProperty] private string _defaultOpenGroup = "all";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyDisplay))]
    private uint _hotkeyModifiers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyDisplay))]
    private uint _hotkeyKey;

    public string HotkeyDisplay => FormatHotkey(HotkeyModifiers, HotkeyKey);

    public string HistoryCapDisplay => $"{HistoryCap:N0} items";

    // Friendly name for the current TTL value (used in the History card subtitle).
    public string TtlDescription => TtlDays switch
    {
        0   => "Unlimited",
        1   => "Day",
        7   => "Week",
        30  => "Month",
        90  => "Quarter",
        180 => "Half a year",
        365 => "Year",
        _   => $"{TtlDays} days",
    };

    // The toggle's subtitle shows only the relevant backdrop name for the
    // current OS — Mica on Win11+ (build ≥ 22000), Acrylic on Win10.
    public string TransparencyDescription =>
        Environment.OSVersion.Version.Build >= 22000
            ? "Use Mica for the settings and popup windows. Turn off to use solid colours."
            : "Use Acrylic for the settings and popup windows. Turn off to use solid colours.";

    public bool   ShowHotkeyConflictWarning => !_hotkey.IsRegistered;
    public string HotkeyConflictWarning =>
        "Could not register this hotkey — both RegisterHotKey and the keyboard hook failed. "
      + "This is rare and may indicate conflicting software (e.g. anti-cheat drivers). "
      + "Try restarting PinBoard, or enable \"Capture Win+V\" as a fallback.";

    public SettingsViewModel(ISettingsService settings, IHistoryStore store, IHotkeyService hotkey)
    {
        _settings         = settings;
        _store            = store;
        _hotkey           = hotkey;
        _historyCap       = settings.HistoryCap;
        _hotkeyModifiers  = settings.HotkeyModifiers;
        _hotkeyKey        = settings.HotkeyKey;
        _useTransparency  = settings.UseTransparency;
        _ttlDays          = settings.TtlDays;
        _hoverSwitchGroup = settings.HoverSwitchGroup;
        _defaultOpenGroup = settings.DefaultOpenGroup;
        _ = LoadStartupStateAsync();
    }

    private async Task LoadStartupStateAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync("PinBoardStartup");
            RunAtStartup = task.State is StartupTaskState.Enabled
                                       or StartupTaskState.EnabledByPolicy;
        }
        catch { RunAtStartup = false; }
    }

    partial void OnHistoryCapChanged(int value) =>
        _settings.HistoryCap = value;

    partial void OnRunAtStartupChanged(bool value) =>
        _ = ApplyStartupAsync(value);

    partial void OnUseTransparencyChanged(bool value)
    {
        _settings.UseTransparency = value;
        App.Current?.ApplyTransparencySetting(value);
    }

    partial void OnTtlDaysChanged(int value)
    {
        _settings.TtlDays = value;
        // Apply immediately — drop any rows that fell past the new cutoff.
        App.Current?.SweepExpiredHistory(value);
    }

    partial void OnHoverSwitchGroupChanged(bool value) =>
        _settings.HoverSwitchGroup = value;

    partial void OnDefaultOpenGroupChanged(string value) =>
        _settings.DefaultOpenGroup = value;

    partial void OnHotkeyModifiersChanged(uint value)
    {
        _settings.HotkeyModifiers = value;
        App.Current?.RebindPrimaryHotkey();
        OnPropertyChanged(nameof(ShowHotkeyConflictWarning));
    }

    partial void OnHotkeyKeyChanged(uint value)
    {
        _settings.HotkeyKey = value;
        App.Current?.RebindPrimaryHotkey();
        OnPropertyChanged(nameof(ShowHotkeyConflictWarning));
    }

    private async Task ApplyStartupAsync(bool enable)
    {
        try
        {
            var task = await StartupTask.GetAsync("PinBoardStartup");
            if (enable)
            {
                var state = await task.RequestEnableAsync();
                if (state is not (StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy))
                    RunAtStartup = false;
            }
            else
            {
                task.Disable();
            }
        }
        catch { RunAtStartup = false; }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        await _store.ClearUnpinnedAsync();
        HistoryJustCleared = true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string FormatHotkey(uint mods, uint key)
    {
        var parts = new List<string>();
        if ((mods & 0x0008) != 0) parts.Add("Win");
        if ((mods & 0x0001) != 0) parts.Add("Alt");
        if ((mods & 0x0002) != 0) parts.Add("Ctrl");
        if ((mods & 0x0004) != 0) parts.Add("Shift");
        parts.Add(((char)key).ToString().ToUpperInvariant());
        return string.Join(" + ", parts);
    }
}
