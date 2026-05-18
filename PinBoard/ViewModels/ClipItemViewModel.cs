using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using PinBoard.Helpers;
using PinBoard.Models;
using PinBoard.Services;

namespace PinBoard.ViewModels;

public sealed partial class ClipItemViewModel : ObservableObject
{
    public ClipItem Model { get; }

    private readonly IClipboardService _clipboard;
    private readonly IPasteService     _paster;
    private readonly IHistoryStore     _store;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinnedVisibility))]
    [NotifyPropertyChangedFor(nameof(PinMenuLabel))]
    [NotifyPropertyChangedFor(nameof(PinMenuGlyph))]
    private bool _pinned;

    public event EventHandler? Deleted;

    [ObservableProperty] private BitmapImage? _thumbnailSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoreFilesVisibility))]
    private string _moreFilesText = string.Empty;

    [ObservableProperty] private IReadOnlyList<string> _fileListPreview = [];

    public Visibility PinnedVisibility =>
        Pinned ? Visibility.Visible : Visibility.Collapsed;

    public string PinMenuLabel => Pinned ? "Unpin" : "Pin";

    // E840 = Pinned (filled pin); E77A = Unpin (pin with slash)
    public string PinMenuGlyph => Pinned ? "" : "";

    public Visibility ImageVisibility =>
        Model.Kind == ClipItemKind.Image ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FilesVisibility =>
        Model.Kind == ClipItemKind.Files ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TextVisibility =>
        Model.Kind is not ClipItemKind.Image and not ClipItemKind.Files
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MoreFilesVisibility =>
        string.IsNullOrEmpty(MoreFilesText) ? Visibility.Collapsed : Visibility.Visible;

    public ClipItemViewModel(ClipItem model,
                             IClipboardService clipboard,
                             IPasteService paster,
                             IHistoryStore store)
    {
        Model     = model;
        _clipboard = clipboard;
        _paster    = paster;
        _store     = store;
        _pinned    = model.Pinned;
    }

    // ── Display properties ───────────────────────────────────────────────────

    public string PreviewText => Model.Preview?.Replace('\n', ' ').Replace('\r', ' ')
                                       ?? (Model.Kind == ClipItemKind.Image ? "[Image]"
                                         : Model.Kind == ClipItemKind.Files ? "[Files]"
                                         : "[Empty]");

    public string TimeAgo
    {
        get
        {
            var elapsed = DateTimeOffset.UtcNow - Model.CreatedAt;
            if (elapsed.TotalSeconds < 60)  return "Just now";
            if (elapsed.TotalMinutes < 60)  return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed.TotalHours   < 24)  return $"{(int)elapsed.TotalHours}h ago";
            return $"{(int)elapsed.TotalDays}d ago";
        }
    }

    public string SourceLine => Model.SourceApp is { Length: > 0 } app
        ? $"{app} · {TimeAgo}"
        : TimeAgo;

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task PasteAsync()
    {
        await EnsureBundleLoadedAsync();
        await _clipboard.SetClipboardAsync(Model, textOnly: false);
        _paster.PasteToStashedWindow();
    }

    [RelayCommand]
    public async Task PasteAsTextAsync()
    {
        await EnsureBundleLoadedAsync();
        await _clipboard.SetClipboardAsync(Model, textOnly: true);
        _paster.PasteToStashedWindow();
    }

    [RelayCommand]
    public async Task TogglePinAsync()
    {
        Pinned = !Pinned;
        Model.Pinned = Pinned;
        await _store.UpdatePinnedAsync(Model.Id, Pinned);
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        await _store.DeleteAsync(Model.Id);
        Deleted?.Invoke(this, EventArgs.Empty);
    }

    // ── Thumbnail / file-list loading ────────────────────────────────────────

    public async Task LoadThumbnailAsync()
    {
        await EnsureBundleLoadedAsync();
        if (Model.Formats is null) return;

        if (Model.Kind == ClipItemKind.Image)
        {
            ThumbnailSource = await DibHelper.ToBitmapAsync(Model.Formats);
        }
        else if (Model.Kind == ClipItemKind.Files)
        {
            try
            {
                if (Model.Formats.Formats.TryGetValue(FormatBundle.FmtHDrop, out var hdrop))
                {
                    var allFiles    = HDropHelper.ParseFiles(hdrop);
                    FileListPreview = allFiles.Take(3).Select(f => Path.GetFileName(f) ?? f).ToList();
                    MoreFilesText   = allFiles.Count > 3 ? $"+{allFiles.Count - 3} more" : string.Empty;
                }
            }
            catch { /* malformed CF_HDROP */ }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task EnsureBundleLoadedAsync()
    {
        if (Model.Formats is not null) return;
        if (Model.PayloadPath is null) return;
        Model.Formats = await BundleStorage.ReadAsync(Model.PayloadPath);
    }
}
