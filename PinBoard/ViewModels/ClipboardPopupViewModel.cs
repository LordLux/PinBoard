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
    private readonly DispatcherQueue   _uiQueue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private ObservableCollection<ClipItemViewModel> _items = new();

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool   _isLoading;

    public bool HasItems => Items.Count > 0;

    public string FooterText => HasItems
        ? $"{Items.Count} item{(Items.Count == 1 ? "" : "s")}"
        : "No clipboard history";

    public ClipboardPopupViewModel(IHistoryStore store,
                                   IClipboardService clipboard,
                                   IPasteService paster)
    {
        _store     = store;
        _clipboard  = clipboard;
        _paster     = paster;
        _uiQueue    = DispatcherQueue.GetForCurrentThread();

        // Listen for new clipboard captures.
        _clipboard.ItemCaptured += OnItemCaptured;
    }

    // Called by the popup before it becomes visible, to refresh the list.
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var items = string.IsNullOrWhiteSpace(SearchQuery)
                ? await _store.GetRecentAsync(200)
                : await _store.SearchAsync(SearchQuery, 100);

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

    private void OnItemCaptured(object? sender, ClipItem item)
    {
        // Add new item to the top of the visible list without a full reload.
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
