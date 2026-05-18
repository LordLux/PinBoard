using System.Collections.Generic;

namespace PinBoard.Services;

public interface IExclusionService
{
    /// Returns true if the clipboard capture should be skipped.
    /// Checks both the sensitive-format flags and the per-app blocklist.
    bool ShouldExclude(nint foregroundHwnd, IReadOnlyCollection<string> clipboardFormatNames);
}
