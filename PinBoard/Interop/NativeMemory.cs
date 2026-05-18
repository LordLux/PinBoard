using System.Runtime.InteropServices;

namespace PinBoard.Interop;

/// Manual P/Invoke for HGLOBAL-based memory and clipboard data operations.
/// CsWin32 wraps HGLOBAL in a SafeHandle which makes raw void* usage awkward;
/// these thin declarations use plain nint to keep call-sites straightforward.
internal static class NativeMemory
{
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe void* GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GlobalFree(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nuint GlobalSize(nint hMem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetClipboardData(uint uFormat, nint hMem);

    /// Allocates moveable global memory, copies <paramref name="bytes"/> into it,
    /// and returns the HGLOBAL handle (as nint). Returns 0 on failure.
    public static unsafe nint AllocAndCopy(byte[] bytes)
    {
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        if (hMem == 0) return 0;

        var ptr = GlobalLock(hMem);
        if (ptr is null) { GlobalFree(hMem); return 0; }

        try
        {
            Marshal.Copy(bytes, 0, (nint)ptr, bytes.Length);
        }
        finally
        {
            GlobalUnlock(hMem);
        }

        return hMem;
    }

    /// Locks, copies to a managed byte array, and unlocks a global memory handle.
    /// Returns null if the handle is invalid.
    public static unsafe byte[]? CopyFromGlobal(nint hMem)
    {
        var ptr = GlobalLock(hMem);
        if (ptr is null) return null;

        try
        {
            var size = (int)GlobalSize(hMem);
            if (size == 0) return null;

            var result = new byte[size];
            Marshal.Copy((nint)ptr, result, 0, size);
            return result;
        }
        finally
        {
            GlobalUnlock(hMem);
        }
    }
}
