namespace PinBoard.Models;

public sealed class ClipItem
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ClipItemKind Kind { get; set; }

    /// Short text extracted for display and FTS indexing (≤ 1 KB).
    public string? Preview { get; set; }

    /// Display name of the app that owned the clipboard at capture time.
    public string? SourceApp { get; set; }

    /// Full path to the source app exe (used to display its icon).
    public string? SourceAppPath { get; set; }

    /// Relative path under LocalFolder\payloads\ for large binary payloads
    /// (images, file lists). Null for small items stored inline in the DB.
    public string? PayloadPath { get; set; }

    /// xxHash64 of the canonical content bytes; used for deduplication.
    public byte[]? Hash { get; set; }

    public bool Pinned { get; set; }

    /// True when the item was flagged sensitive at capture time and was
    /// encrypted before being written to disk.
    public bool Sensitive { get; set; }

    /// Full multi-format payload preserved from the original clipboard
    /// event. Loaded on-demand; may be null when only the preview is needed.
    public FormatBundle? Formats { get; set; }
}
