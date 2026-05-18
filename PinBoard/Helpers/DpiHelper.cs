using Windows.Win32;
using Windows.Win32.Foundation;

namespace PinBoard.Helpers;

internal static class DpiHelper
{
    public const uint DefaultDpi = 96;

    public static double ScaleFactor(uint dpi) => dpi / (double)DefaultDpi;

    public static int LogicalToPhysical(double logical, uint dpi) =>
        (int)Math.Round(logical * ScaleFactor(dpi));

    public static double PhysicalToLogical(int physical, uint dpi) =>
        physical / ScaleFactor(dpi);

    public static uint GetDpiForWindow(nint hwnd)
    {
        var dpi = PInvoke.GetDpiForWindow((HWND)hwnd);
        return dpi > 0 ? dpi : DefaultDpi;
    }
}
