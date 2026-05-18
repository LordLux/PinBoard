using Microsoft.UI.Dispatching;
using PinBoard.Helpers;
using PinBoard.Interop;
using PinBoard.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace PinBoard.Services;

/// Monitors the system clipboard via WinRT Clipboard.ContentChanged,
/// captures all formats into a FormatBundle, and raises ItemCaptured on the UI thread.
///
/// WinRT ContentChanged is used for notification because AddClipboardFormatListener +
/// SetWindowSubclass on a WinUI 3 HWND does not reliably deliver WM_CLIPBOARDUPDATE
/// through the XAML framework's message dispatch.  After notification, we still read
/// all formats via Win32 (EnumClipboardFormats / GlobalLock) to preserve every format.
public sealed class ClipboardService : IClipboardService
{
    // Standard format IDs that are NOT HGLOBAL-based and cannot be read via GlobalLock.
    // Skip these; rely on their HGLOBAL-based equivalents (e.g. CF_DIB instead of CF_BITMAP).
    private static readonly HashSet<uint> SkipFormats = new() { 2, 3, 9, 10, 14 };
    // CF_BITMAP=2, CF_METAFILEPICT=3, CF_PALETTE=9, CF_PENDATA=10, CF_ENHMETAFILE=14

    // Sensitive-format names: if any of these are on the clipboard, skip persistence.
    private static readonly HashSet<string> SensitiveFormatNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExcludeClipboardContentFromMonitorProcessing",
        "CanIncludeInClipboardHistory",
        "CanUploadToCloudClipboard",
        "Clipboard Viewer Ignore",
    };

    private readonly IExclusionService _exclusions;
    private readonly DispatcherQueue   _uiQueue;

    private HWND _hwnd;

    public event EventHandler<ClipItem>? ItemCaptured;

    public ClipboardService(IExclusionService exclusions)
    {
        _exclusions = exclusions;
        _uiQueue    = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException(
                             "ClipboardService must be created on the UI thread.");
    }

    public void StartMonitoring(nint hwnd)
    {
        _hwnd = (HWND)hwnd;
        // Subscribe on the UI thread — ContentChanged fires on the UI thread.
        Clipboard.ContentChanged += OnClipboardContentChanged;
    }

    public void StopMonitoring()
    {
        Clipboard.ContentChanged -= OnClipboardContentChanged;
    }

    private void OnClipboardContentChanged(object? sender, object e)
    {
        // Snapshot the foreground window before yielding to the thread pool,
        // so we record which app triggered the copy.
        var sourceHwnd = PInvoke.GetForegroundWindow();
        _ = Task.Run(() => CaptureAsync(sourceHwnd));
    }

    private async Task CaptureAsync(HWND sourceHwnd)
    {
        FormatBundle? bundle = null;
        List<string>  formatNames = new();
        bool sensitive = false;

        // Open clipboard with retry — other apps may hold it briefly.
        bool opened = false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (PInvoke.OpenClipboard(_hwnd))
            {
                opened = true;
                break;
            }
            await Task.Delay(60);
        }
        if (!opened) return;

        try
        {
            bundle = new FormatBundle();

            uint fmt = 0;
            while ((fmt = PInvoke.EnumClipboardFormats(fmt)) != 0)
            {
                string name = GetFormatName(fmt);
                formatNames.Add(name);

                if (SensitiveFormatNames.Contains(name))
                {
                    sensitive = true;
                    continue;
                }

                if (SkipFormats.Contains(fmt)) continue;

                var bytes = ReadFormatBytes(fmt);
                if (bytes is not null)
                    bundle.Formats[name] = bytes;
            }
        }
        finally
        {
            PInvoke.CloseClipboard();
        }

        if (bundle is null || bundle.Formats.Count == 0) return;

        // Exclusion check — run BEFORE we compute the heavy hash.
        if (_exclusions.ShouldExclude(sourceHwnd, formatNames)) return;

        // Determine kind and extract preview.
        var kind    = DetectKind(bundle);
        var preview = ExtractPreview(bundle, kind);
        if (preview is null && kind == ClipItemKind.Text) return; // Nothing useful

        var (appName, appPath) = SourceAppHelper.GetSourceApp(sourceHwnd);

        var hash = ComputeHash(bundle, kind);

        var item = new ClipItem
        {
            CreatedAt    = DateTimeOffset.UtcNow,
            Kind         = kind,
            Preview      = preview,
            SourceApp    = appName,
            SourceAppPath = appPath,
            Hash         = hash,
            Sensitive    = sensitive,
            Formats      = bundle,
        };

        _uiQueue.TryEnqueue(() => ItemCaptured?.Invoke(this, item));
    }

    public Task SetClipboardAsync(ClipItem item, bool textOnly = false)
    {
        if (item.Formats is null) return Task.CompletedTask;

        bool opened = false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (PInvoke.OpenClipboard(_hwnd)) { opened = true; break; }
            Thread.Sleep(60);
        }
        if (!opened) return Task.CompletedTask;

        try
        {
            PInvoke.EmptyClipboard();

            foreach (var (name, bytes) in item.Formats.Formats)
            {
                if (textOnly && name != FormatBundle.FmtUnicodeText && name != FormatBundle.FmtText)
                    continue;

                uint fmtId = name.StartsWith("CF_", StringComparison.Ordinal)
                    ? GetStandardFormatId(name)
                    : PInvoke.RegisterClipboardFormat(name);

                if (fmtId == 0) continue;

                WriteFormatBytes(fmtId, bytes);
            }
        }
        finally
        {
            PInvoke.CloseClipboard();
        }

        return Task.CompletedTask;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string GetFormatName(uint fmt)
    {
        if (fmt >= 0xC000)
        {
            // Registered format — get its string name.
            unsafe
            {
                char* buf = stackalloc char[256];
                int len = PInvoke.GetClipboardFormatName(fmt, buf, 256);
                if (len > 0) return new string(buf, 0, len);
            }
        }
        // Standard formats — use canonical names.
        return fmt switch
        {
            1  => FormatBundle.FmtText,
            7  => "CF_OEMTEXT",
            13 => FormatBundle.FmtUnicodeText,
            8  => FormatBundle.FmtDib,
            17 => "CF_DIBV5",
            15 => FormatBundle.FmtHDrop,
            16 => "CF_LOCALE",
            _  => $"CF_{fmt}",
        };
    }

    private static byte[]? ReadFormatBytes(uint fmt)
    {
        var hData = NativeMemory.GetClipboardData(fmt);
        if (hData == 0) return null;
        return NativeMemory.CopyFromGlobal(hData);
    }

    private static void WriteFormatBytes(uint fmtId, byte[] bytes)
    {
        var hMem = NativeMemory.AllocAndCopy(bytes);
        if (hMem == 0) return;
        NativeMemory.SetClipboardData(fmtId, hMem);
        // Windows owns hMem after successful SetClipboardData — do NOT free it.
    }

    private static ClipItemKind DetectKind(FormatBundle b)
    {
        if (b.HasImage) return ClipItemKind.Image;
        if (b.HasFiles) return ClipItemKind.Files;
        if (b.HasRich)  return ClipItemKind.Rich;
        return ClipItemKind.Text;
    }

    private static string? ExtractPreview(FormatBundle b, ClipItemKind kind)
    {
        if (kind == ClipItemKind.Image) return "[Image]";
        if (kind == ClipItemKind.Files) return "[Files]";

        if (b.Formats.TryGetValue(FormatBundle.FmtUnicodeText, out var utf16))
        {
            // CF_UNICODETEXT is null-terminated UTF-16LE.
            var text = System.Text.Encoding.Unicode.GetString(utf16).TrimEnd('\0');
            return text.Length > 1024 ? text[..1024] : text;
        }

        if (b.Formats.TryGetValue(FormatBundle.FmtText, out var ansi))
        {
            var text = System.Text.Encoding.Default.GetString(ansi).TrimEnd('\0');
            return text.Length > 1024 ? text[..1024] : text;
        }

        return null;
    }

    private static byte[] ComputeHash(FormatBundle b, ClipItemKind kind)
    {
        // Hash the primary content bytes for deduplication.
        byte[] content = kind switch
        {
            ClipItemKind.Image => b.Formats.TryGetValue(FormatBundle.FmtDib, out var d) ? d : Array.Empty<byte>(),
            _                  => b.Formats.TryGetValue(FormatBundle.FmtUnicodeText, out var t) ? t : Array.Empty<byte>(),
        };

        return System.Security.Cryptography.SHA1.HashData(content);
    }

    private static uint GetStandardFormatId(string name) => name switch
    {
        "CF_TEXT"        => 1,
        "CF_OEMTEXT"     => 7,
        "CF_UNICODETEXT" => 13,
        "CF_DIB"         => 8,
        "CF_DIBV5"       => 17,
        "CF_HDROP"       => 15,
        "CF_LOCALE"      => 16,
        _                => 0,
    };
}
