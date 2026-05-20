using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using PinBoard.Interop;
using PinBoard.Services;
using PinBoard.ViewModels;
using PinBoard.Views;

namespace PinBoard;

public partial class App : Application
{
    public static new App? Current => (App?)Application.Current;

    public IServiceProvider Services { get; }
    private ClipboardPopup? _popup;
    private SettingsWindow? _settingsWindow;

    private const int HotkeyId = 1;

    public App()
    {
        Services = BuildServiceProvider();
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            WriteCrashLog(e.Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            WriteCrashLog(e.Exception);
        };
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            var dir  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PinBoard");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* logging must never throw */ }
    }

    private static IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IExclusionService, ExclusionService>();
        services.AddSingleton<IHistoryStore, SqliteHistoryStore>();
        services.AddSingleton<LowLevelKeyboardHook>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<IWindowPositioner, WindowPositioner>();
        services.AddSingleton<IPasteService, PasteService>();

        services.AddTransient<ClipboardPopupViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var store     = Services.GetRequiredService<IHistoryStore>();
            await store.InitializeAsync();

            var settings  = Services.GetRequiredService<ISettingsService>();
            var clipboard = Services.GetRequiredService<IClipboardService>();
            var viewModel = Services.GetRequiredService<ClipboardPopupViewModel>();

            _popup = new ClipboardPopup(
                viewModel,
                Services.GetRequiredService<IHotkeyService>(),
                Services.GetRequiredService<IWindowPositioner>(),
                settings,
                Services.GetRequiredService<IPasteService>());

            clipboard.ItemCaptured += async (_, item) =>
            {
                item.Id = await store.AddAsync(item);
                await store.EvictOldestAsync(settings.HistoryCap);
            };

            _popup.Activate();

            clipboard.StartMonitoring(_popup.Hwnd);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
        }
    }

    public void ShowPopup()   => _popup?.ShowPopup();
    public void HidePopup()   => _popup?.HidePopup();
    public void TogglePopup() => _popup?.TogglePopup();

    internal void DispatchShowPopup() =>
        _popup?.DispatcherQueue.TryEnqueue(() => _popup.ShowPopup());

    public void ShowSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var viewModel = Services.GetRequiredService<SettingsViewModel>();
        _settingsWindow = new SettingsWindow(viewModel);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
    }

    /// Applies the UseTransparency setting to both windows live.
    public void ApplyTransparencySetting(bool useTransparency)
    {
        _settingsWindow?.ApplyTransparency(useTransparency);
        _popup?.ApplyTransparency(useTransparency);
    }

    /// Re-registers the primary hotkey (called after the user rebinds it in Settings).
    public void RebindPrimaryHotkey()
    {
        if (_popup is null) return;
        var hotkey   = Services.GetRequiredService<IHotkeyService>();
        var settings = Services.GetRequiredService<ISettingsService>();
        hotkey.Unregister(_popup.Hwnd, HotkeyId);
        hotkey.Register(_popup.Hwnd, HotkeyId, settings.HotkeyModifiers, settings.HotkeyKey);
    }

}
