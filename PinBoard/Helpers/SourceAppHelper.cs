using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace PinBoard.Helpers;

internal static class SourceAppHelper
{
    private static readonly Dictionary<int, (string Name, string Path)> _cache = new();

    /// Returns (display name, exe path) for the process owning the given HWND.
    /// Returns ("Unknown", "") if the process cannot be identified.
    public static unsafe (string Name, string Path) GetSourceApp(HWND hwnd)
    {
        if (hwnd == default) return ("Unknown", "");

        uint pid = 0;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        if (pid == 0) return ("Unknown", "");

        if (_cache.TryGetValue((int)pid, out var cached))
            return cached;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            var mainModule = proc.MainModule;
            if (mainModule is null) return ("Unknown", "");

            var path = mainModule.FileName ?? "";
            var name = Path.GetFileNameWithoutExtension(path);

            // Use the file description from the version info if available.
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
                name = info.FileDescription;

            var result = (name, path);
            _cache[(int)pid] = result;
            return result;
        }
        catch
        {
            return ("Unknown", "");
        }
    }

    public static void ClearCache() => _cache.Clear();
}
