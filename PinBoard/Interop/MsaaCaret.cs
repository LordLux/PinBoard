using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PinBoard.Interop;

// Retrieves the text-caret screen rect via MSAA (oleacc.dll).
// Uses AccessibleObjectFromWindow(OBJID_CARET) on the focused HWND.
// Works for Visual Studio, classic Win32, and some Electron apps where
// UIA TextPattern returns null.  Coordinates are already screen pixels;
// no ClientToScreen conversion needed.
internal static class MsaaCaret
{
    private static readonly Guid IID_IAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private const uint OBJID_CARET = 0xFFFFFFF8; // (LONG)-8 cast to DWORD

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        HWND hwnd,
        uint dwObjectId,
        in Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppvObject);

    public static unsafe RectInt32? GetCaretRect()
    {
        try
        {
            var fg = PInvoke.GetForegroundWindow();
            if (fg == default) return null;

            uint pid;
            var tid = PInvoke.GetWindowThreadProcessId(fg, &pid);
            if (tid == 0) return null;

            GUITHREADINFO gui = default;
            gui.cbSize = (uint)sizeof(GUITHREADINFO);
            // hwndFocus is the exact child HWND with keyboard focus (e.g. the Chrome renderer).
            // Fall back to the foreground window if GetGUIThreadInfo fails.
            var hwnd = PInvoke.GetGUIThreadInfo(tid, &gui) && gui.hwndFocus != default
                ? gui.hwndFocus
                : fg;

            int hr = AccessibleObjectFromWindow(hwnd, OBJID_CARET, IID_IAccessible, out var acc);
            if (hr < 0 || acc is null) return null;

            // IAccessible is a dual interface; dynamic dispatch uses IDispatch.Invoke via
            // GetIDsOfNames("accLocation") so we don't need to hand-count the vtable.
            // CHILDID_SELF = 0 passed as the varChild VARIANT.
            dynamic dynAcc = acc;
            dynAcc.accLocation(out int x, out int y, out int w, out int h, 0);

            if (w <= 0 || h <= 0) return null;
            return new RectInt32(x, y, w, h);
        }
        catch
        {
            return null;
        }
    }
}
