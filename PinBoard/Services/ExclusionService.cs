using PinBoard.Helpers;
using Windows.Win32.Foundation;

namespace PinBoard.Services;

public sealed class ExclusionService : IExclusionService
{
    // Clipboard format names whose mere PRESENCE means "don't capture this item."
    // "CanIncludeInClipboardHistory" / "CanUploadToCloudClipboard" are intentionally
    // omitted: those are DWORD-valued flags where the value (0 = exclude, 1 = include)
    // matters, not just the presence.  Checking only by name would incorrectly exclude
    // items that apps explicitly mark as safe to include in history.
    private static readonly HashSet<string> ExcludeOnPresence =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ExcludeClipboardContentFromMonitorProcessing",
            "Clipboard Viewer Ignore",
        };

    private readonly ISettingsService _settings;

    public ExclusionService(ISettingsService settings) => _settings = settings;

    public bool ShouldExclude(nint foregroundHwnd,
                              IReadOnlyCollection<string> clipboardFormatNames)
    {
        // Check opt-out clipboard format flags.
        foreach (var fmt in clipboardFormatNames)
            if (ExcludeOnPresence.Contains(fmt)) return true;

        // Check per-app exclusion list.
        if (foregroundHwnd != 0 && _settings.ExcludedApps.Count > 0)
        {
            var (_, exePath) = SourceAppHelper.GetSourceApp((HWND)foregroundHwnd);
            if (!string.IsNullOrEmpty(exePath))
            {
                foreach (var excluded in _settings.ExcludedApps)
                    if (excluded.Equals(exePath, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
        }

        return false;
    }
}
