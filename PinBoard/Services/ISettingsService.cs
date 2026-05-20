namespace PinBoard.Services;

public interface ISettingsService
{
    /// Modifier flags for the primary hotkey (MOD_WIN | MOD_SHIFT by default).
    uint HotkeyModifiers { get; set; }

    /// Virtual-key code for the primary hotkey ('V' by default → Win+Shift+V).
    uint HotkeyKey { get; set; }

    /// When true, PinBoard is registered as a startup task.
    bool RunAtLogin { get; set; }

    /// Maximum number of unpinned history items to retain.
    int HistoryCap { get; set; }

    /// When true, the popup window stays open after a paste (pin-window mode).
    bool IsPinned { get; set; }

    /// When true, both windows draw a translucent Mica/Acrylic backdrop.
    /// When false, they use solid colours (matches the look before Mica
    /// was introduced).
    bool UseTransparency { get; set; }

    /// Per-app exclusion list (source-app exe paths, case-insensitive).
    IReadOnlyList<string> ExcludedApps { get; }

    void AddExcludedApp(string exePath);
    void RemoveExcludedApp(string exePath);

    void Save();
}
