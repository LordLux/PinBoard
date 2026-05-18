using System.Runtime.InteropServices;
using PinBoard.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace PinBoard.Services;

/// Tier-A global hotkey via RegisterHotKey on a dedicated STA thread (best effort).
/// Tier-B: always installs a WH_KEYBOARD_LL trigger via LowLevelKeyboardHook so the
/// hotkey fires even when explorer.exe intercepts the Win+key combination.
///
/// HotkeyPressed is de-duplicated: if both paths fire within 150 ms, the second is dropped.
public sealed class HotkeyService : IHotkeyService
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_QUIT   = 0x0012;

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private readonly LowLevelKeyboardHook _llHook;

    private Thread? _thread;
    private uint    _threadId;
    private int     _id;
    private bool    _disposed;
    private Guid    _triggerId;

    private long _lastFiredTick; // for de-duplication

    public event EventHandler? HotkeyPressed;

    public bool IsRegistered { get; private set; }

    public HotkeyService(LowLevelKeyboardHook llHook)
    {
        _llHook = llHook;
    }

    public bool Register(nint hwnd, int id, uint modifiers, uint vkKey)
    {
        if (_thread is not null) return false; // already registered

        _id = id;

        // Tier A — RegisterHotKey (best effort; may fail if Explorer owns the combo).
        using var ready   = new ManualResetEventSlim(false);
        bool      tierA   = false;

        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            tierA = PInvoke.RegisterHotKey(
                default,
                id,
                (HOT_KEY_MODIFIERS)modifiers,
                vkKey);

            ready.Set();
            if (!tierA) return;

            unsafe
            {
                Windows.Win32.UI.WindowsAndMessaging.MSG msg;
                while (PInvoke.GetMessage(&msg, default, 0, 0))
                {
                    if (msg.message == WM_HOTKEY && (nuint)msg.wParam == (nuint)(uint)_id)
                        FireHotkeyPressed();

                    PInvoke.TranslateMessage(in msg);
                    PInvoke.DispatchMessage(in msg);
                }
            }
        });

        _thread.IsBackground = true;
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Name = "PinBoard-Hotkey";
        _thread.Start();

        ready.Wait(TimeSpan.FromSeconds(2));

        // Tier B — LL hook trigger (always; works even when Tier A fails).
        bool tierB = _llHook.Install();
        if (tierB)
            _triggerId = _llHook.AddTrigger(modifiers, vkKey, swallowKey: true, FireHotkeyPressed);

        // "Is reachable" if either path is alive.
        IsRegistered = tierA || tierB;
        return IsRegistered;
    }

    public void Unregister(nint hwnd, int id)
    {
        IsRegistered = false;

        if (_triggerId != default)
        {
            _llHook.RemoveTrigger(_triggerId);
            _triggerId = default;
        }

        if (_threadId != 0)
        {
            PInvoke.PostThreadMessage(_threadId, WM_QUIT, 0, 0);
            _thread?.Join(TimeSpan.FromSeconds(1));
        }
        _thread   = null;
        _threadId = 0;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Unregister(0, _id);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void FireHotkeyPressed()
    {
        // De-duplicate: if both Tier-A (RegisterHotKey) and Tier-B (LL hook) fire
        // for the same keystroke, ignore the second within 150 ms.
        long now  = Environment.TickCount64;
        long prev = Interlocked.Exchange(ref _lastFiredTick, now);
        if (now - prev < 150) return;

        HotkeyPressed?.Invoke(this, EventArgs.Empty);
    }
}
