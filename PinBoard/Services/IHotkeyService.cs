namespace PinBoard.Services;

public interface IHotkeyService : IDisposable
{
    /// Fired when the registered hotkey is detected (via RegisterHotKey or LL hook).
    event EventHandler? HotkeyPressed;

    /// True when the hotkey is reachable via at least one path (RegisterHotKey or LL hook).
    bool IsRegistered { get; }

    /// Registers the hotkey. Returns false only if both Tier-A and the LL hook failed.
    bool Register(nint hwnd, int id, uint modifiers, uint vkKey);

    void Unregister(nint hwnd, int id);
}
