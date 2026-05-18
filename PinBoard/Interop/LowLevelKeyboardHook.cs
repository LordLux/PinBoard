using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace PinBoard.Interop;

/// WH_KEYBOARD_LL hook on a dedicated STA thread with its own message loop.
/// Supports multiple configurable triggers (modifier + VK combinations).
/// A trigger optionally swallows the keystroke so it never reaches Explorer.
public sealed class LowLevelKeyboardHook : IDisposable
{
    // ── Win32 constants ───────────────────────────────────────────────────────
    private const int  WH_KEYBOARD_LL = 13;
    private const uint WM_KEYDOWN     = 0x0100;
    private const uint WM_SYSKEYDOWN  = 0x0104;
    private const uint WM_QUIT        = 0x0012;
    private const int  VK_LWIN        = 0x5B;
    private const int  VK_RWIN        = 0x5C;
    private const int  VK_LSHIFT      = 0xA0;
    private const int  VK_RSHIFT      = 0xA1;
    private const int  VK_LCONTROL    = 0xA2;
    private const int  VK_RCONTROL    = 0xA3;
    private const int  VK_LMENU       = 0xA4;  // Alt
    private const int  VK_RMENU       = 0xA5;
    private const int  VK_F23         = 0x86;   // harmless dummy to cancel Win-key Start Menu
    private const uint LLKHF_INJECTED = 0x10;

    // HOT_KEY_MODIFIERS bit layout (matches RegisterHotKey and SettingsService):
    private const uint MOD_ALT   = 0x0001;
    private const uint MOD_CTRL  = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN   = 0x0008;

    // ── Raw P/Invoke ──────────────────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int idHook, HookDelegate lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT_RAW
    {
        public uint  vkCode;
        public uint  scanCode;
        public uint  flags;
        public uint  time;
        public nuint dwExtraInfo;
    }

    private delegate nint HookDelegate(int nCode, nuint wParam, nint lParam);

    // ── Trigger model ─────────────────────────────────────────────────────────
    private sealed record Trigger(uint Modifiers, uint Vk, bool Swallow, Action OnPressed);

    // ── State ─────────────────────────────────────────────────────────────────
    private nint          _hookHandle;
    private uint          _hookThreadId;
    private Thread?       _hookThread;
    private HookDelegate? _hookProc;  // keep delegate alive (GC guard)
    private bool          _disposed;

    private readonly ConcurrentDictionary<Guid, Trigger> _triggers = new();

    // ── Public API ────────────────────────────────────────────────────────────

    /// Installs the hook. Idempotent — safe to call multiple times.
    public bool Install()
    {
        if (_hookHandle != 0) return true;

        using var ready = new ManualResetEventSlim(false);
        bool success = false;

        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            _hookProc     = HookProc;

            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, 0, 0);
            success     = _hookHandle != 0;
            ready.Set();

            if (!success) return;

            unsafe
            {
                Windows.Win32.UI.WindowsAndMessaging.MSG msg;
                while (PInvoke.GetMessage(&msg, default, 0, 0))
                {
                    PInvoke.TranslateMessage(in msg);
                    PInvoke.DispatchMessage(in msg);
                }
            }
        });

        _hookThread.IsBackground = true;
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Name = "PinBoard-LLKBHook";
        _hookThread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));

        return success;
    }

    /// Registers a trigger. Returns a Guid that can be passed to RemoveTrigger.
    /// <paramref name="modifiers"/> uses the same bit layout as RegisterHotKey:
    ///   MOD_ALT=1, MOD_CTRL=2, MOD_SHIFT=4, MOD_WIN=8.
    public Guid AddTrigger(uint modifiers, uint vk, bool swallowKey, Action onPressed)
    {
        var id = Guid.NewGuid();
        _triggers[id] = new Trigger(modifiers, vk, swallowKey, onPressed);
        return id;
    }

    /// Removes a previously registered trigger. No-op if the id is unknown.
    public void RemoveTrigger(Guid id) => _triggers.TryRemove(id, out _);

    public void Uninstall()
    {
        _triggers.Clear();
        if (_hookHandle != 0)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = 0;
        }
        if (_hookThreadId != 0)
        {
            PInvoke.PostThreadMessage(_hookThreadId, WM_QUIT, 0, 0);
            _hookThread?.Join(TimeSpan.FromSeconds(1));
            _hookThreadId = 0;
            _hookThread   = null;
        }
        _hookProc = null;
    }

    public void Dispose()
    {
        if (!_disposed) { _disposed = true; Uninstall(); }
    }

    // ── Hook procedure ────────────────────────────────────────────────────────

    private unsafe nint HookProc(int nCode, nuint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var kb = (KBDLLHOOKSTRUCT_RAW*)lParam;

            // Skip events we injected ourselves (e.g. the F23 start-menu cancel).
            if ((kb->flags & LLKHF_INJECTED) == 0)
            {
                uint heldMods = GetHeldModifiers();
                uint pressedVk = kb->vkCode;

                foreach (var trigger in _triggers.Values)
                {
                    if (trigger.Vk == pressedVk && trigger.Modifiers == heldMods)
                    {
                        trigger.OnPressed();

                        if (trigger.Swallow)
                        {
                            if ((heldMods & MOD_WIN) != 0)
                                CancelWinKeyStartMenu();
                            return 1; // swallow
                        }
                    }
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static uint GetHeldModifiers()
    {
        uint m = 0;
        if (IsDown(VK_LWIN)     || IsDown(VK_RWIN))    m |= MOD_WIN;
        if (IsDown(VK_LSHIFT)   || IsDown(VK_RSHIFT))  m |= MOD_SHIFT;
        if (IsDown(VK_LCONTROL) || IsDown(VK_RCONTROL)) m |= MOD_CTRL;
        if (IsDown(VK_LMENU)    || IsDown(VK_RMENU))   m |= MOD_ALT;
        return m;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void CancelWinKeyStartMenu()
    {
        var inputs = new INPUT[2];

        inputs[0].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[0].Anonymous.ki.wVk = (VIRTUAL_KEY)VK_F23;

        inputs[1].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[1].Anonymous.ki.wVk    = (VIRTUAL_KEY)VK_F23;
        inputs[1].Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;

        PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
    }
}
