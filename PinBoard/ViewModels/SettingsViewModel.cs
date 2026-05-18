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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyDisplay))]
    private uint _hotkeyModifiers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyDisplay))]
    private uint _hotkeyKey;

    public string HotkeyDisplay => FormatHotkey(HotkeyModifiers, HotkeyKey);

    public string HistoryCapDisplay => $"{HistoryCap:N0} items";

    public bool   ShowHotkeyConflictWarning => !_hotkey.IsRegistered;
    public string HotkeyConflictWarning =>
        "Could not register this hotkey — both RegisterHotKey and the keyboard hook failed. "
      + "This is rare and may indicate conflicting software (e.g. anti-cheat drivers). "
      + "Try restarting PinBoard, or enable \"Capture Win+V\" as a fallback.";

    public SettingsViewModel(ISettingsService settings, IHistoryStore store, IHotkeyService hotkey)
    {
        _settings        = settings;
        _store           = store;
        _hotkey          = hotkey;
        _historyCap      = settings.HistoryCap;
        _hotkeyModifiers = settings.HotkeyModifiers;
        _hotkeyKey       = settings.HotkeyKey;
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
