using Windows.Storage;

namespace PinBoard.Services;

public sealed class SettingsService : ISettingsService
{
    private static ApplicationDataContainer Store =>
        ApplicationData.Current.LocalSettings;

    // All properties auto-persist on set via Store.Values[key].
    // Stored as the exact CLR types LocalSettings supports:
    //   uint → UInt32, int → Int32, bool → Boolean, string → String.

    public uint HotkeyModifiers
    {
        get => Get("HotkeyModifiers", 0x000Cu);
        set => Store.Values["HotkeyModifiers"] = value;
    }

    public uint HotkeyKey
    {
        get => Get("HotkeyKey", 0x56u); // 'V'
        set => Store.Values["HotkeyKey"] = value;
    }

    public bool RunAtLogin
    {
        get => Get("RunAtLogin", false);
        set => Store.Values["RunAtLogin"] = value;
    }

    public int HistoryCap
    {
        get => Get("HistoryCap", 5000);
        set => Store.Values["HistoryCap"] = value;
    }

    public bool IsPinned
    {
        get => Get("IsPinned", false);
        set => Store.Values["IsPinned"] = value;
    }

    private readonly List<string> _excludedApps;

    public IReadOnlyList<string> ExcludedApps => _excludedApps;

    public SettingsService()
    {
        var raw = Get<string?>("ExcludedApps", null);
        _excludedApps = string.IsNullOrEmpty(raw)
            ? new List<string>()
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public void AddExcludedApp(string exePath)
    {
        if (!_excludedApps.Contains(exePath, StringComparer.OrdinalIgnoreCase))
        {
            _excludedApps.Add(exePath);
            PersistExcludedApps();
        }
    }

    public void RemoveExcludedApp(string exePath)
    {
        if (_excludedApps.RemoveAll(x =>
                x.Equals(exePath, StringComparison.OrdinalIgnoreCase)) > 0)
            PersistExcludedApps();
    }

    // No-op: all properties auto-save; kept for interface compatibility.
    public void Save() { }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static T Get<T>(string key, T defaultValue)
    {
        if (Store.Values.TryGetValue(key, out var val) && val is T typed)
            return typed;
        return defaultValue;
    }

    private void PersistExcludedApps() =>
        Store.Values["ExcludedApps"] = string.Join(';', _excludedApps);
}
