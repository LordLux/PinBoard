using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace PinBoard.Services;

/// Stashes the foreground window before the popup shows, then after the
/// clipboard is set it restores focus and simulates Ctrl+V via SendInput.
public sealed class PasteService : IPasteService
{
    private HWND _stashedHwnd;

    public void StashForegroundWindow()
    {
        _stashedHwnd = PInvoke.GetForegroundWindow();
    }

    public void PasteToStashedWindow()
    {
        if (_stashedHwnd != default)
        {
            PInvoke.SetForegroundWindow(_stashedHwnd);
            // Give the target window a moment to gain focus before sending keys.
            Thread.Sleep(50);
        }

        SendCtrlV();
    }

    private static unsafe void SendCtrlV()
    {
        var inputs = new INPUT[4];

        inputs[0].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[0].Anonymous.ki.wVk = VIRTUAL_KEY.VK_CONTROL;

        inputs[1].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[1].Anonymous.ki.wVk = VIRTUAL_KEY.VK_V;

        inputs[2].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[2].Anonymous.ki.wVk    = VIRTUAL_KEY.VK_V;
        inputs[2].Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;

        inputs[3].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[3].Anonymous.ki.wVk    = VIRTUAL_KEY.VK_CONTROL;
        inputs[3].Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;

        PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
    }
}
