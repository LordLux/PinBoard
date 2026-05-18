namespace PinBoard.Services;

public interface IPasteService
{
    /// Saves the current foreground window handle so we can restore focus
    /// after the popup takes it.
    void StashForegroundWindow();

    /// Hides the popup, restores focus to the stashed window, then
    /// sends Ctrl+V via SendInput to trigger the paste.
    void PasteToStashedWindow();
}
