using PinBoard.Helpers;
using PinBoard.Interop;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PinBoard.Services;

/// Determines where to place the popup using a cascading fallback chain:
/// 1. UIA TextPattern caret rect       (Chromium, modern Office, WinUI/UWP apps)
/// 2. GetGUIThreadInfo caret rect      (Win32/WinForms/WPF classic caret)
/// 3. MSAA OBJID_CARET                 (Visual Studio, some Electron apps)
/// 4. Bottom-center of current monitor (when no caret info is available)
/// All coordinates are physical pixels — the same space AppWindow.MoveAndResize expects.
public sealed class WindowPositioner : IWindowPositioner
{
    private const int Offset = 8; // gap between caret/anchor and popup edge, in physical pixels

    private static readonly string TraceLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PinBoard", "positioner_trace.log");

    private static void Trace(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TraceLog)!);
            File.AppendAllText(TraceLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    public PointInt32 GetPopupPosition(SizeInt32 popupSize)
    {
        string step = "Default";
        PointInt32 anchor;

        // Caret-based probes return the raw screen rect so we can decide above/below.
        RectInt32? caretRect = null;
        if (TryUiaCaretRect() is { } uia)         { caretRect = uia;  step = "UIA"; }
        else if (TryClassicCaretRect() is { } cl)  { caretRect = cl;   step = "Classic"; }
        else if (TryMsaaCaretRect() is { } msaa)   { caretRect = msaa; step = "MSAA"; }

        if (caretRect is { } cr)
        {
            anchor = SmartPlace(cr, popupSize);
        }
        else
        {
            anchor = DefaultAnchor(popupSize);
        }

        var result = Clamp(anchor, popupSize);
        Trace($"step={step} anchor={anchor.X},{anchor.Y} result={result.X},{result.Y}");
        return result;
    }

    // ── Smart above/below placement ──────────────────────────────────────────

    private static PointInt32 SmartPlace(RectInt32 caret, SizeInt32 popup)
    {
        var work      = MonitorHelper.GetWorkArea(new PointInt32(caret.X, caret.Y));
        int below     = work.Y + work.Height - (caret.Y + caret.Height);
        int above     = caret.Y - work.Y;

        // Prefer below; flip above only when the popup doesn't fit below but fits above.
        int y = below >= popup.Height + Offset || below >= above
            ? caret.Y + caret.Height + Offset
            : caret.Y - Offset - popup.Height;

        return new PointInt32(caret.X, y);
    }

    // ── Caret-rect probes (return raw screen rect) ───────────────────────────

    private static RectInt32? TryUiaCaretRect()
        => UiAutomationCaret.GetCaretRect(TimeSpan.FromMilliseconds(80));

    private static unsafe RectInt32? TryClassicCaretRect()
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

        // rcCaret is in client coords of hwndCaret; translate origin to screen.
        var origin = new System.Drawing.Point(r.left, r.top);
        if (!PInvoke.ClientToScreen(gui.hwndCaret, ref origin)) return null;

        return new RectInt32(origin.X, origin.Y, r.right - r.left, r.bottom - r.top);
    }

    private static RectInt32? TryMsaaCaretRect()
        => MsaaCaret.GetCaretRect();

    private static PointInt32 DefaultAnchor(SizeInt32 popup)
    {
        // Bottom-center of the monitor the foreground window is on.
        var hint = new PointInt32(0, 0);
        var fg   = PInvoke.GetForegroundWindow();
        if (fg != default && PInvoke.GetWindowRect(fg, out RECT fgRect))
            hint = new PointInt32((fgRect.left + fgRect.right) / 2, (fgRect.top + fgRect.bottom) / 2);

        var work = MonitorHelper.GetWorkArea(hint);
        int x    = work.X + (work.Width  - popup.Width)  / 2;
        int y    = work.Y +  work.Height - popup.Height  - Offset;
        return new PointInt32(x, y);
    }

    // ── Monitor clamping ─────────────────────────────────────────────────────

    private static PointInt32 Clamp(PointInt32 anchor, SizeInt32 popup)
    {
        var rect    = new RectInt32(anchor.X, anchor.Y, popup.Width, popup.Height);
        var clamped = MonitorHelper.ClampToWorkArea(rect, anchor);
        return new PointInt32(clamped.X, clamped.Y);
    }
}
