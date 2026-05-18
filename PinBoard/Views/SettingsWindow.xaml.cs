using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using PinBoard.ViewModels;
using Windows.Graphics;
using WinRT.Interop;

namespace PinBoard.Views;

public sealed partial class SettingsWindow : Window
{
    private const int WindowWidth  = 520;
    private const int WindowHeight = 480;

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
            p.IsResizable  = false;
            p.IsMaximizable = false;
        }

        Title = "PinBoard Settings";
    }
}
