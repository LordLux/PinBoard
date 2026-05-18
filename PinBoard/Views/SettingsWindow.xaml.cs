using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PinBoard.ViewModels;
using Windows.Graphics;
using WinRT.Interop;

namespace PinBoard.Views;

public sealed partial class SettingsWindow : Window
{
    private const int WindowWidth  = 820;
    private const int WindowHeight = 600;

    public SettingsViewModel ViewModel { get; }

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ConfigureWindow();
    }

    private void ConfigureWindow()
    {
        var hwnd      = WindowNative.GetWindowHandle(this);
        var windowId  = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        appWindow.Resize(new SizeInt32(WindowWidth, WindowHeight));
        appWindow.SetPresenter(OverlappedPresenter.Create());

        if (appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable   = true;
            p.IsMaximizable = false;
        }

        Title = "PinBoard Settings";

        // Mica is Windows 11 only (build 22000+); fall back to Desktop Acrylic
        // on Windows 10 since Mica there just renders as a solid colour.
        SystemBackdrop = Environment.OSVersion.Version.Build >= 22000
            ? new MicaBackdrop()
            : new DesktopAcrylicBackdrop();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var tb = appWindow.TitleBar;
        tb.BackgroundColor               = Colors.Transparent;
        tb.InactiveBackgroundColor       = Colors.Transparent;
        tb.ButtonBackgroundColor         = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;

        var tag = item.Tag as string ?? "general";
        GeneralPage.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        HotkeyPage.Visibility  = tag == "hotkey"  ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = tag == "history" ? Visibility.Visible : Visibility.Collapsed;

        PageTitle.Text = item.Content?.ToString() ?? "Settings";
    }
}
