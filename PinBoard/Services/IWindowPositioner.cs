using Windows.Graphics;

namespace PinBoard.Services;

public interface IWindowPositioner
{
    /// Returns the screen-space position (physical pixels) where the popup
    /// should appear, clamped to the work area of the nearest monitor.
    PointInt32 GetPopupPosition(SizeInt32 popupSize);
}
