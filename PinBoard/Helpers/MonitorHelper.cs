using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace PinBoard.Helpers;

internal static class MonitorHelper
{
    /// Returns the work area of the monitor nearest to <paramref name="anchorPoint"/>.
    public static unsafe RectInt32 GetWorkArea(PointInt32 anchorPoint)
    {
        var hMonitor = PInvoke.MonitorFromPoint(
            new System.Drawing.Point(anchorPoint.X, anchorPoint.Y),
            MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        MONITORINFO mi = default;
        mi.cbSize = (uint)sizeof(MONITORINFO);

        if (PInvoke.GetMonitorInfo(hMonitor, &mi))
            return new RectInt32(mi.rcWork.left, mi.rcWork.top,
                                 mi.rcWork.right  - mi.rcWork.left,
                                 mi.rcWork.bottom - mi.rcWork.top);

        return new RectInt32(0, 0, 1920, 1040);
    }

    /// Returns a copy of <paramref name="popup"/> clamped so it fits entirely
    /// inside the work area of the monitor nearest to <paramref name="anchorPoint"/>.
    public static unsafe RectInt32 ClampToWorkArea(RectInt32 popup, PointInt32 anchorPoint)
    {
        // MonitorFromPoint takes System.Drawing.Point (CsWin32 maps Win32 POINT to it).
        var hMonitor = PInvoke.MonitorFromPoint(
            new System.Drawing.Point(anchorPoint.X, anchorPoint.Y),
            MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        MONITORINFO mi = default;
        mi.cbSize = (uint)sizeof(MONITORINFO);

        RECT work;
        if (PInvoke.GetMonitorInfo(hMonitor, &mi))
            work = mi.rcWork;
        else
            work = new RECT { left = 0, top = 0, right = 1920, bottom = 1040 };

        var x = Math.Clamp(popup.X, work.left, work.right  - popup.Width);
        var y = Math.Clamp(popup.Y, work.top,  work.bottom - popup.Height);
        return new RectInt32(x, y, popup.Width, popup.Height);
    }
}
