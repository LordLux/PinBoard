using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PinBoard.Models;
using PinBoard.Services;

namespace PinBoard.ViewModels;

public sealed partial class ClipboardPopupViewModel : ObservableObject
{
    private readonly IHistoryStore     _store;
    private readonly IClipboardService _clipboard;
    private readonly IPasteService     _paster;
    private readonly ISettingsService  _settings;
    private readonly DispatcherQueue   _uiQueue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private ObservableCollection<ClipItemViewModel> _items = new();

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool   _isLoading;

    // One of "all" / "text" / "image" / "files" / "collect". Seeded from
    // DefaultOpenGroup (resolving "last" → LastSelectedGroup) on construction.
    [ObservableProperty] private string _selectedGroup = "all";

    public bool HasItems => Items.Count > 0;

    public string FooterText => HasItems
        ? $"{Items.Count} item{(Items.Count == 1 ? "" : "s")}"
        : "No clipboard history";

    public ClipboardPopupViewModel(IHistoryStore store,
                                   IClipboardService clipboard,
                                   IPasteService paster,
                                   ISettingsService settings)
    {
        _store      = store;
        _clipboard  = clipboard;
        _paster     = paster;
        _settings   = settings;
        _uiQueue    = DispatcherQueue.GetForCurrentThread();

        _selectedGroup = ResolveInitialGroup();

        // Listen for new clipboard captures.
        _clipboard.ItemCaptured += OnItemCaptured;
    }

    // Called by the popup before it becomes visible. Resets the selected
    // group according to the user's "Default open group" preference so the
    // popup opens consistently.
    public void ApplyDefaultGroupOnOpen()
    {
        var def = _settings.DefaultOpenGroup;
        if (def == "last")
            SelectedGroup = _settings.LastSelectedGroup;
        else
            SelectedGroup = def;
    }

    private string ResolveInitialGroup()
    {
        var def = _settings.DefaultOpenGroup;
        return def == "last" ? _settings.LastSelectedGroup : def;
    }

    // Called by the popup before it becomes visible, to refresh the list.
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var (kinds, pinnedOnly) = MapGroupToFilter(SelectedGroup);

            var items = string.IsNullOrWhiteSpace(SearchQuery)
                ? await _store.GetRecentAsync(200, kinds, pinnedOnly)
                : await _store.SearchAsync(SearchQuery, 100, kinds, pinnedOnly);

            Items = new ObservableCollection<ClipItemViewModel>(
                items.Select(CreateVm));

            _ = Task.WhenAll(Items
                .Where(vm => vm.Model.Kind is ClipItemKind.Image or ClipItemKind.Files)
                .Select(vm => vm.LoadThumbnailAsync()));

            OnPropertyChanged(nameof(FooterText));
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        _ = RefreshAsync();
    }

    partial void OnSelectedGroupChanged(string value)
    {
        _settings.LastSelectedGroup = value;
        _ = RefreshAsync();
    }

    private void OnItemCaptured(object? sender, ClipItem item)
    {
        // Only surface the new item if it matches the current filter — otherwise
        // it'll show up on the next refresh / group switch.
        if (!ItemMatchesCurrentGroup(item)) return;

        var vm = CreateVm(item);
        _uiQueue.TryEnqueue(() =>
        {
            // Remove a duplicate if it already shows (the store deduplicates by hash,
            // but the in-memory list may still have the old entry at a lower position).
            var dup = Items.FirstOrDefault(x => x.Model.Preview == item.Preview
                                             && x.Model.Kind     == item.Kind);
            if (dup is not null) Items.Remove(dup);

            Items.Insert(0, vm);
            if (vm.Model.Kind is ClipItemKind.Image or ClipItemKind.Files)
                _ = vm.LoadThumbnailAsync();
            OnPropertyChanged(nameof(FooterText));
            OnPropertyChanged(nameof(HasItems));
        });
    }

    // Called from the popup window on paste-gesture (click / Enter / number key).
    public async Task PasteItemAsync(ClipItemViewModel vm, bool asText = false)
    {
        // The store/paste command handles focus restoration.
        if (asText)
            await vm.PasteAsTextAsync();
        else
            await vm.PasteAsync();
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        await _store.ClearUnpinnedAsync();
        await RefreshAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Maps a group tag to (kinds, pinnedOnly) for store queries.
    // "text" includes both Text and Rich because rich content is still text
    // from the user's perspective.
    private static (IReadOnlyCollection<ClipItemKind>? kinds, bool pinnedOnly)
        MapGroupToFilter(string group) => group switch
    {
        "text"    => (new[] { ClipItemKind.Text, ClipItemKind.Rich }, false),
        "image"   => (new[] { ClipItemKind.Image },                   false),
        "files"   => (new[] { ClipItemKind.Files },                   false),
        "collect" => (null,                                            true),
        _         => (null,                                            false),  // "all"
    };

    private bool ItemMatchesCurrentGroup(ClipItem item) => SelectedGroup switch
    {
        "text"    => item.Kind is ClipItemKind.Text or ClipItemKind.Rich,
        "image"   => item.Kind is ClipItemKind.Image,
        "files"   => item.Kind is ClipItemKind.Files,
        "collect" => item.Pinned,
        _         => true,
    };

    private ClipItemViewModel CreateVm(ClipItem item)
    {
        var vm = new ClipItemViewModel(item, _clipboard, _paster, _store);
        vm.Deleted += OnItemDeleted;
        return vm;
    }

    private void OnItemDeleted(object? sender, EventArgs _)
    {
        if (sender is not ClipItemViewModel vm) return;
        _uiQueue.TryEnqueue(() =>
        {
            vm.Deleted -= OnItemDeleted;
            Items.Remove(vm);
            OnPropertyChanged(nameof(FooterText));
            OnPropertyChanged(nameof(HasItems));
        });
    }
}
