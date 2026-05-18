using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace PinBoard.Interop;

/// Wraps SetWindowSubclass / RemoveWindowSubclass (Comctl32) to intercept
/// Win32 messages on a WinUI 3 window's HWND. WinUI 3 does not expose a
/// WndProc override, so subclassing is the only supported mechanism.
///
/// Usage:
///   var sub = new WindowSubclass(hwnd, id: 1, onMessage: (msg, wp, lp) => { ... return null; });
///   // return null to fall through to the default proc, or return a value to handle it.
///   sub.Dispose(); // removes the subclass
internal sealed class WindowSubclass : IDisposable
{
    private readonly HWND _hwnd;
    private readonly nuint _subclassId;
    private readonly SUBCLASSPROC _proc;
    private bool _disposed;

    // Callback receives (hwnd, msg, wParam, lParam).
    // Return non-null to short-circuit the default proc.
    public delegate nint? MessageHandler(uint msg, nuint wParam, nint lParam);

    public WindowSubclass(nint hwnd, uint subclassId, MessageHandler handler)
    {
        _hwnd = (HWND)hwnd;
        _subclassId = subclassId;

        // Keep a delegate reference alive for the lifetime of this object —
        // the unmanaged side calls it; GC must not collect it.
        _proc = Callback;

        bool ok = PInvoke.SetWindowSubclass(_hwnd, _proc, _subclassId, 0);
        if (!ok)
            throw new InvalidOperationException("SetWindowSubclass failed.");

        Handler = handler;
    }

    public MessageHandler Handler { get; set; }

    private LRESULT Callback(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam,
                              nuint uIdSubclass, nuint dwRefData)
    {
        var result = Handler(msg, wParam.Value, lParam.Value);
        if (result.HasValue)
            return (LRESULT)result.Value;

        return PInvoke.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PInvoke.RemoveWindowSubclass(_hwnd, _proc, _subclassId);
    }
}
