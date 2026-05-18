using System.Runtime.InteropServices;
using Windows.Graphics;

namespace PinBoard.Interop;

// Trace log: %LOCALAPPDATA%\PinBoard\uia_trace.log
// Each line is flushed before the next COM call so a native crash leaves
// the last-successfully-written line as the pinpointed failure site.

/// Probes the focused element via IUIAutomation.GetFocusedElement() and its
/// TextPattern to obtain the on-screen caret bounding rectangle.
///
/// Runs on a short-lived background MTA thread and hard-aborts on timeout.
///
/// Returns the bottom-left corner of the first text-range bounding rect
/// (in physical screen pixels, matching AppWindow.MoveAndResize coordinates).
/// Returns null on timeout, failure, or when the element has no TextPattern.
internal static class UiAutomationCaret
{
    // CUIAutomation8 coclass CLSID — available on Windows 8+ (we target Win10+).
    private static readonly Guid CUIAutomation8Clsid = new("E22AD333-B25F-460C-83D0-0581107395C9");
    private const int UIA_TextPatternId = 10014;

    private static readonly string TraceLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PinBoard", "uia_trace.log");

    private static void Trace(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TraceLog)!);
            File.AppendAllText(TraceLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    public static RectInt32? GetCaretRect(TimeSpan timeout)
    {
        // UIA probe runs on a background MTA thread with a hard timeout, so a
        // deadlock (e.g. VS debugging cross-process COM) times out after 80 ms
        // and returns null — the main thread is never blocked.
        RectInt32? result = null;

        var thread = new Thread(() =>
        {
            try { result = Probe(); }
            catch { /* any COM / interop failure → treat as "no caret" */ }
        })
        {
            IsBackground = true,
            Name         = "PinBoard-UIA",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        return thread.Join(timeout) ? result : null;
    }

    // ── Internal probe ────────────────────────────────────────────────────────

    private static RectInt32? Probe()
    {
        Trace("--- Probe start ---");

        Trace("GetTypeFromCLSID");
        var uiType = Type.GetTypeFromCLSID(CUIAutomation8Clsid);
        Trace($"GetTypeFromCLSID => {(uiType is null ? "null" : "ok")}");
        if (uiType is null) return null;

        Trace("Activator.CreateInstance");
        var instance = Activator.CreateInstance(uiType);
        Trace($"CreateInstance => {(instance is null ? "null" : instance.GetType().Name)}");
        if (instance is not IUIAutomation ui) { Trace("cast to IUIAutomation failed"); return null; }

        Trace("GetFocusedElement");
        var element = ui.GetFocusedElement();
        Trace($"GetFocusedElement => {(element is null ? "null" : "ok")}");
        if (element is null) return null;

        Trace("GetCurrentPattern");
        var rawPattern = element.GetCurrentPattern(UIA_TextPatternId);
        Trace($"GetCurrentPattern => {(rawPattern is null ? "null" : rawPattern.GetType().Name)}");
        if (rawPattern is not IUIAutomationTextPattern textPattern) { Trace("no TextPattern"); return null; }

        Trace("GetSelection");
        var ranges = textPattern.GetSelection();
        Trace($"GetSelection => {(ranges is null ? "null" : "ok")}");
        if (ranges is null) return null;

        Trace("get_Length");
        var len = ranges.get_Length();
        Trace($"get_Length => {len}");
        if (len == 0) return null;

        Trace("GetElement(0)");
        var range = ranges.GetElement(0);
        Trace($"GetElement(0) => {(range is null ? "null" : "ok")}");
        if (range is null) return null;

        Trace("GetBoundingRectangles");
        var rects = range.GetBoundingRectangles();
        Trace($"GetBoundingRectangles => {(rects is null ? "null" : $"len={rects.Length}")}");
        if (rects is null || rects.Length < 4) return null;

        double x = rects[0], y = rects[1], w = rects[2], h = rects[3];
        Trace($"rect => x={x} y={y} w={w} h={h}");

        // Degenerate range (caret with no selection) returns a zero-width rect.
        // Expand to one character and retry.  For a real selection the rect is
        // already non-zero and we skip this step — first rect = selection start.
        if (w <= 0 || h <= 0)
        {
            Trace("ExpandToEnclosingUnit(Character)");
            try { range.ExpandToEnclosingUnit(4); }
            catch { Trace("ExpandToEnclosingUnit threw"); return null; }

            rects = range.GetBoundingRectangles();
            Trace($"GetBoundingRectangles (after expand) => {(rects is null ? "null" : $"len={rects.Length}")}");
            if (rects is null || rects.Length < 4) return null;

            x = rects[0]; y = rects[1]; w = rects[2]; h = rects[3];
            Trace($"rect (after expand) => x={x} y={y} w={w} h={h}");
            if (w <= 0 || h <= 0) return null;
        }

        Trace("--- Probe success ---");
        return new RectInt32((int)x, (int)y, (int)w, (int)h);
    }

    // ── Minimal COM interfaces ────────────────────────────────────────────────
    // Each stub method corresponds to one vtable slot we don't need.
    // Stubs are never called; they just ensure the vtable offsets align.

    [ComImport]
    [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        // vtable[3]  CompareElements
        [PreserveSig] int _CompareElements();
        // vtable[4]  CompareRuntimeIds
        [PreserveSig] int _CompareRuntimeIds();
        // vtable[5]  GetRootElement
        [PreserveSig] int _GetRootElement();
        // vtable[6]  ElementFromHandle  ← present in the real header between GetRootElement and ElementFromPoint
        [PreserveSig] int _ElementFromHandle();
        // vtable[7]  ElementFromPoint
        [PreserveSig] int _ElementFromPoint();
        // vtable[8]  GetFocusedElement — we call this
        IUIAutomationElement? GetFocusedElement();
        // Rest of the interface is omitted; we stop after the method we need.
    }

    [ComImport]
    [Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        // vtable[3]  SetFocus
        [PreserveSig] int _SetFocus();
        // vtable[4]  GetRuntimeId
        [PreserveSig] int _GetRuntimeId();
        // vtable[5]  FindFirst
        [PreserveSig] int _FindFirst();
        // vtable[6]  FindAll
        [PreserveSig] int _FindAll();
        // vtable[7]  FindFirstBuildCache
        [PreserveSig] int _FindFirstBuildCache();
        // vtable[8]  FindAllBuildCache
        [PreserveSig] int _FindAllBuildCache();
        // vtable[9]  BuildUpdatedCache
        [PreserveSig] int _BuildUpdatedCache();
        // vtable[10] GetCurrentPropertyValue
        [PreserveSig] int _GetCurrentPropertyValue();
        // vtable[11] GetCurrentPropertyValueEx
        [PreserveSig] int _GetCurrentPropertyValueEx();
        // vtable[12] GetCachedPropertyValue
        [PreserveSig] int _GetCachedPropertyValue();
        // vtable[13] GetCachedPropertyValueEx
        [PreserveSig] int _GetCachedPropertyValueEx();
        // vtable[14] GetCurrentPatternAs
        [PreserveSig] int _GetCurrentPatternAs();
        // vtable[15] GetCachedPatternAs
        [PreserveSig] int _GetCachedPatternAs();
        // vtable[16] GetCurrentPattern — we call this
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object? GetCurrentPattern(int patternId);
    }

    [ComImport]
    [Guid("32EFA686-3C99-4F31-8787-C45B3BBBC4D6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextPattern
    {
        // vtable[3]  GetSelection — we call this
        IUIAutomationTextRangeArray? GetSelection();
    }

    [ComImport]
    [Guid("CE4AE76A-E717-4C98-81EA-47371D028EB6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRangeArray
    {
        // vtable[3]  get_Length
        int get_Length();
        // vtable[4]  GetElement
        IUIAutomationTextRange? GetElement(int index);
    }

    [ComImport]
    [Guid("A543CC6A-F4AE-494B-8239-C814481187A8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRange
    {
        // vtable[3]  Clone
        [PreserveSig] int _Clone();
        // vtable[4]  Compare
        [PreserveSig] int _Compare();
        // vtable[5]  CompareEndpoints
        [PreserveSig] int _CompareEndpoints();
        // vtable[6]  ExpandToEnclosingUnit — called with TextUnit_Character (4) before GetBoundingRectangles
        void ExpandToEnclosingUnit(int textUnit);
        // vtable[7]  FindAttribute
        [PreserveSig] int _FindAttribute();
        // vtable[8]  FindText
        [PreserveSig] int _FindText();
        // vtable[9]  GetAttributeValue
        [PreserveSig] int _GetAttributeValue();
        // vtable[10] GetBoundingRectangles — we call this
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_R8)]
        double[]? GetBoundingRectangles();
    }
}
