using System.Collections.Generic;

namespace PinBoard.Models;

/// Preserves ALL clipboard formats exactly as captured, keyed by Win32 format
/// name. Registered formats use their string name (e.g. "HTML Format", "PNG");
/// standard formats use a canonical string constant (e.g. "CF_UNICODETEXT").
///
/// Storing every format is the only way to reconstruct a complex paste (e.g.
/// an Excel selection that carries BIFF12 + HTML + RTF + CSV + image) so that
/// pasting from history feels identical to pasting the original copy.
public sealed class FormatBundle
{
    // Standard format name constants used as dictionary keys.
    public const string FmtUnicodeText = "CF_UNICODETEXT";
    public const string FmtText        = "CF_TEXT";
    public const string FmtBitmap      = "CF_BITMAP";
    public const string FmtDib         = "CF_DIB";
    public const string FmtHDrop       = "CF_HDROP";
    public const string FmtHtml        = "HTML Format";
    public const string FmtRtf         = "Rich Text Format";
    public const string FmtPng         = "PNG";

    public Dictionary<string, byte[]> Formats { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasText  => Formats.ContainsKey(FmtUnicodeText) || Formats.ContainsKey(FmtText);
    public bool HasImage => Formats.ContainsKey(FmtDib)  || Formats.ContainsKey(FmtBitmap) || Formats.ContainsKey(FmtPng);
    public bool HasFiles => Formats.ContainsKey(FmtHDrop);
    public bool HasRich  => Formats.ContainsKey(FmtHtml) || Formats.ContainsKey(FmtRtf);
}
