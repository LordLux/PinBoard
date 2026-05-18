using System.Threading.Tasks;
using PinBoard.Models;

namespace PinBoard.Services;

public interface IClipboardService
{
    /// Fired on the UI thread whenever a new (non-excluded) item is captured.
    event EventHandler<ClipItem>? ItemCaptured;

    /// Installs AddClipboardFormatListener on the given message-pump HWND.
    void StartMonitoring(nint hwnd);

    void StopMonitoring();

    /// Writes all formats from the item's FormatBundle back to the clipboard,
    /// then optionally narrows to text-only for plain-text paste.
    Task SetClipboardAsync(ClipItem item, bool textOnly = false);
}
