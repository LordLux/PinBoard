using PinBoard.Helpers;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PinBoard.Services;

/// Determines where to place the popup using a cascading fallback chain:
/// 1. UIA TextPattern caret rect       (Chromium, modern Office, WinUI/UWP apps)
/// 2. GetGUIThreadInfo caret rect      (Win32/WinForms/WPF classic caret)
/// 3. GetCursorPos                     (mouse cursor — reliable, not text-aware)
/// 4. Foreground window bottom edge
/// 5. Primary monitor work-area corner
/// All coordinates are physical pixels — the same space AppWindow.MoveAndResize expects.
public sealed class WindowPositioner : IWindowPositioner
{
    private const int Offset = 8; // gap between anchor point and popup edge, in physical pixels

    public PointInt32 GetPopupPosition(SizeInt32 popupSize)
    {
        var anchor = TryClassicCaretAnchor()
                  ?? TryCursorAnchor()
                  ?? TryForegroundWindowAnchor()
                  ?? new PointInt32(16, 16);

        return Clamp(anchor, popupSize);
    }

    // ── Anchor probes ────────────────────────────────────────────────────────

    private static unsafe PointInt32? TryClassicCaretAnchor()
    {
        var fg = PInvoke.GetForegroundWindow();
        if (fg == default) return null;

        uint pid;
        var tid = PInvoke.GetWindowThreadProcessId(fg, &pid);
        if (tid == 0) return null;

        GUITHREADINFO gui = default;
        gui.cbSize = (uint)sizeof(GUITHREADINFO);
        if (!PInvoke.GetGUIThreadInfo(tid, &gui)) return null;
        if (gui.hwndCaret == default) return null;

        var r = gui.rcCaret;
        if (r.right <= r.left || r.bottom <= r.top) return null;

        // rcCaret is in client coords of hwndCaret; convert bottom-left to screen.
        var pt = new System.Drawing.Point(r.left, r.bottom + Offset);
        if (!PInvoke.ClientToScreen(gui.hwndCaret, ref pt)) return null;

        return new PointInt32(pt.X, pt.Y);
    }

    private static PointInt32? TryForegroundWindowAnchor()
    {
        var fg = PInvoke.GetForegroundWindow();
        if (fg == default) return null;

        if (!PInvoke.GetWindowRect(fg, out RECT r)) return null;
        return new PointInt32(r.left, r.bottom + Offset);
    }

    private static PointInt32? TryCursorAnchor()
    {
        if (!PInvoke.GetCursorPos(out System.Drawing.Point pt)) return null;
        return new PointInt32(pt.X + Offset, pt.Y + Offset);
    }

    // ── Monitor clamping ─────────────────────────────────────────────────────

    private static PointInt32 Clamp(PointInt32 anchor, SizeInt32 popup)
    {
        var rect    = new RectInt32(anchor.X, anchor.Y, popup.Width, popup.Height);
        var clamped = MonitorHelper.ClampToWorkArea(rect, anchor);
        return new PointInt32(clamped.X, clamped.Y);
    }
}
