using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PinBoard.Interop;
using PinBoard.ViewModels;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;

namespace PinBoard.Views;

public sealed partial class SettingsWindow : Window
{
    private const int InitialWidth  = 820;
    private const int InitialHeight = 600;
    private const int MinWidthDip   = 500;
    private const int MinHeightDip  = 300;

    // DWMWA_USE_IMMERSIVE_DARK_MODE: makes DWM render the residual non-client
    // frame in dark so a white 1-2px line doesn't show through. Documented
    // since Win10 build 19041.
    private const int  DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int  DWMWA_BORDER_COLOR            = 34;
    private const uint DWMWA_COLOR_NONE              = 0xFFFFFFFE;

    private const uint WM_NCCALCSIZE    = 0x0083;
    private const uint WM_GETMINMAXINFO = 0x0024;

    // Segoe MDL2 Assets glyphs for the maximize button.  = ChromeMaximize
    // (a square outline),  = ChromeRestore (overlapping squares — shown
    // when the window is already maximized).
    private const string GlyphMaximize = "";
    private const string GlyphRestore  = "";

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);

    public SettingsViewModel ViewModel { get; }

    private AppWindow                    _appWindow = null!;
    private OverlappedPresenter          _presenter = null!;
    private WindowSubclass?              _subclass;
    private InputNonClientPointerSource? _ncInput;
    private IntPtr                       _hwnd;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        _currentPage = PageGeneral;
        // Seed nav selection so General is highlighted on first open.
        ApplyNavSelection("general");
        ConfigureWindow();
        InitializeAboutPage();
        Closed += (_, _) => { _subclass?.Dispose(); _subclass = null; };
    }

    private void InitializeAboutPage()
    {
        try
        {
            var pkg = Windows.ApplicationModel.Package.Current;
            var v = pkg.Id.Version;
            VersionText.Text = $"Version {v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch
        {
            VersionText.Text = "Version unknown";
        }
    }

    // Nav tile selection — manual Grid-based tiles. ApplyNavSelection sets
    // each tile's Bg/Bar Opacity directly so selection is deterministic,
    // sidestepping the WinUI 3 RadioButton VisualState quirk where Checked
    // setters fail to re-apply after the first state transition.
    private FrameworkElement? _currentPage;
    private string            _currentTag = "general";

    private void NavTile_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement tile) return;
        var tag = tile.Tag as string ?? "general";
        if (tag == _currentTag) return;

        FrameworkElement target = tag switch
        {
            "general" => PageGeneral,
            "hotkey"  => PageHotkey,
            "history" => PageHistory,
            "about"   => PageAbout,
            _         => PageGeneral,
        };

        var previous = _currentPage;
        _currentPage = target;
        _currentTag  = tag;

        ApplyNavSelection(tag);
        AnimateSwipe(previous, target);
    }

    // Set the visual selection state on all four nav tiles based on which
    // one is currently active.
    private void ApplyNavSelection(string tag)
    {
        SetTileSelected(NavGeneralBg, NavGeneralBar, tag == "general");
        SetTileSelected(NavHotkeyBg,  NavHotkeyBar,  tag == "hotkey");
        SetTileSelected(NavHistoryBg, NavHistoryBar, tag == "history");
        SetTileSelected(NavAboutBg,   NavAboutBar,   tag == "about");
    }

    private static void SetTileSelected(FrameworkElement bg, FrameworkElement bar, bool selected)
    {
        bg.Opacity  = selected ? 1.0 : 0.0;
        bar.Opacity = selected ? 1.0 : 0.0;
    }

    // Hover feedback — applied via the same Bg Border at reduced opacity,
    // but only when the tile isn't already the active selection.
    private void NavTile_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement tile) return;
        var tag = tile.Tag as string;
        if (tag == _currentTag) return;
        SetTileHover(tag, true);
    }

    private void NavTile_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement tile) return;
        var tag = tile.Tag as string;
        if (tag == _currentTag) return;
        SetTileHover(tag, false);
    }

    private void SetTileHover(string? tag, bool hovered)
    {
        FrameworkElement? bg = tag switch
        {
            "general" => NavGeneralBg,
            "hotkey"  => NavHotkeyBg,
            "history" => NavHistoryBg,
            "about"   => NavAboutBg,
            _         => null,
        };
        if (bg is not null) bg.Opacity = hovered ? 0.5 : 0.0;
    }

    private int GetPageIndex(FrameworkElement? page)
    {
        if (page == PageGeneral) return 0;
        if (page == PageHotkey)  return 1;
        if (page == PageHistory) return 2;
        if (page == PageAbout)   return 3;
        return -1;
    }

    private void AnimateSwipe(FrameworkElement? oldPage, FrameworkElement newPage)
    {
        // Slide distance = viewport height so each page fully exits/enters.
        // Fall back to a generous fixed value before layout is measured.
        double distance = ContentScroll.ActualHeight;
        if (distance < 100) distance = 600;

        // Direction: navigating DOWN the list (newIdx > oldIdx) keeps the
        // existing upward-swipe (old exits top, new enters from bottom).
        // Navigating UP the list reverses: old exits bottom, new enters
        // from top. When there's no old page (first-ever switch), default
        // to the forward direction so the page slides up into view.
        int oldIdx = GetPageIndex(oldPage);
        int newIdx = GetPageIndex(newPage);
        bool forward = oldPage is null || newIdx >= oldIdx;
        double sign = forward ? 1 : -1;
        double outTo  = -distance * sign;
        double inFrom =  distance * sign;

        var duration = TimeSpan.FromMilliseconds(320);
        var ease     = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var sb = new Storyboard();

        // OUTGOING
        if (oldPage is not null)
        {
            oldPage.IsHitTestVisible = false;
            var outT = EnsureTranslate(oldPage);
            outT.Y = 0;

            var outAnim = new DoubleAnimation
            {
                From = 0, To = outTo,
                Duration = duration,
                EasingFunction = ease,
            };
            Storyboard.SetTarget(outAnim, outT);
            Storyboard.SetTargetProperty(outAnim, "Y");
            sb.Children.Add(outAnim);
        }

        // INCOMING — preposition off-screen on the opposite side, then slide to 0.
        var inT = EnsureTranslate(newPage);
        inT.Y = inFrom;
        newPage.Visibility = Visibility.Visible;
        newPage.IsHitTestVisible = true;

        var inAnim = new DoubleAnimation
        {
            From = inFrom, To = 0,
            Duration = duration,
            EasingFunction = ease,
        };
        Storyboard.SetTarget(inAnim, inT);
        Storyboard.SetTargetProperty(inAnim, "Y");
        sb.Children.Add(inAnim);

        var captured = oldPage;
        sb.Completed += (_, _) =>
        {
            if (captured is not null)
            {
                captured.Visibility = Visibility.Collapsed;
                captured.IsHitTestVisible = true;
                if (captured.RenderTransform is TranslateTransform t)
                    t.Y = 0;
            }
        };

        sb.Begin();
    }

    private static TranslateTransform EnsureTranslate(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform t) return t;
        t = new TranslateTransform();
        element.RenderTransform = t;
        return t;
    }

    private void ConfigureWindow()
    {
        _hwnd        = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow   = AppWindow.GetFromWindowId(windowId);

        _presenter = OverlappedPresenter.Create();
        _presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        _presenter.IsResizable   = true;
        _presenter.IsMaximizable = true;
        _presenter.IsMinimizable = true;
        _appWindow.SetPresenter(_presenter);

        _appWindow.Resize(new SizeInt32(InitialWidth, InitialHeight));

        Title = "PinBoard Settings";

        int useDark = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

        uint borderColor = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(uint));

        ApplyRoundedCorners(_hwnd);

        // Subclass first so WM_NCCALCSIZE / WM_GETMINMAXINFO interception is
        // active when SetWindowPos(SWP_FRAMECHANGED) forces the system to
        // recompute the frame geometry. Order matters here.
        _subclass = new WindowSubclass(_hwnd, subclassId: 1, OnMessage);

        PInvoke.SetWindowPos(
            (HWND)_hwnd, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);

        _ncInput = InputNonClientPointerSource.GetForWindowId(windowId);

        // AppWindow.Changed fires on size/position transitions — including
        // when the OS maximize/restore happens via a drag-region double-
        // click, which never enters our XAML button handler. Use it to
        // keep the maximize-button glyph in sync.
        _appWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange) UpdateMaximizeGlyph();
        };
    }

    private nint? OnMessage(uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_NCCALCSIZE:
                // wParam != 0 → collapse non-client area to nothing so the
                // top white-strip artifact (microsoft-ui-xaml #8947) is gone.
                if (wParam != 0) return 0;
                break;

            case WM_GETMINMAXINFO:
            {
                // MINMAXINFO is in physical pixels.
                uint dpi = PInvoke.GetDpiForWindow((HWND)_hwnd);
                double s = dpi / 96.0;
                var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);

                info.ptMinTrackSize.X = (int)(MinWidthDip  * s);
                info.ptMinTrackSize.Y = (int)(MinHeightDip * s);

                // Maximize geometry: with WM_NCCALCSIZE collapsed to zero,
                // the default maximize behaviour spills past the monitor
                // edges (the OS expects the frame to absorb that overflow)
                // AND covers the taskbar. Override with the monitor's
                // work-area rect — ptMaxPosition is relative to the
                // monitor origin, ptMaxSize is the work-area dimensions.
                var monitor = PInvoke.MonitorFromWindow(
                    (HWND)_hwnd,
                    MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (PInvoke.GetMonitorInfo(monitor, ref mi))
                {
                    info.ptMaxPosition.X = mi.rcWork.left - mi.rcMonitor.left;
                    info.ptMaxPosition.Y = mi.rcWork.top  - mi.rcMonitor.top;
                    info.ptMaxSize.X     = mi.rcWork.right  - mi.rcWork.left;
                    info.ptMaxSize.Y     = mi.rcWork.bottom - mi.rcWork.top;
                }

                Marshal.StructureToPtr(info, lParam, false);
                return 0;
            }
        }
        return null;
    }

    private static void ApplyRoundedCorners(IntPtr hwnd)
    {
        var pref = (uint)DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        unsafe
        {
            PInvoke.DwmSetWindowAttribute(
                (HWND)hwnd,
                DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE,
                &pref, sizeof(uint));
        }
    }

    private void DragRegion_Loaded(object sender, RoutedEventArgs e) =>
        UpdateDragRegion();

    private void DragRegion_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateDragRegion();

    // Register TWO caption regions: the title-bar area in the left column
    // (full width of the column) and the right-column overlay (full width
    // minus the CaptionButtons). Both are passed to SetRegionRects in a
    // single array. Coordinates are physical pixels relative to the
    // window's top-left.
    private void UpdateDragRegion()
    {
        if (_ncInput is null || DragRegionLeft.XamlRoot is null) return;

        double scale = DragRegionLeft.XamlRoot.RasterizationScale;

        var leftBounds = DragRegionLeft
            .TransformToVisual(null)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, DragRegionLeft.ActualWidth, DragRegionLeft.ActualHeight));
        var leftRect = new RectInt32(
            _X:      (int)System.Math.Round(leftBounds.X * scale),
            _Y:      (int)System.Math.Round(leftBounds.Y * scale),
            _Width:  (int)System.Math.Round(leftBounds.Width  * scale),
            _Height: (int)System.Math.Round(leftBounds.Height * scale));

        var rightBounds = DragRegionRight
            .TransformToVisual(null)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, DragRegionRight.ActualWidth, DragRegionRight.ActualHeight));
        double buttonsWidth = CaptionButtons.ActualWidth;
        double rightDragWidth = System.Math.Max(0, rightBounds.Width - buttonsWidth);
        var rightRect = new RectInt32(
            _X:      (int)System.Math.Round(rightBounds.X * scale),
            _Y:      (int)System.Math.Round(rightBounds.Y * scale),
            _Width:  (int)System.Math.Round(rightDragWidth * scale),
            _Height: (int)System.Math.Round(rightBounds.Height * scale));

        _ncInput.SetRegionRects(NonClientRegionKind.Caption, new[] { leftRect, rightRect });
    }

    private void UpdateMaximizeGlyph()
    {
        MaximizeGlyph.Glyph = _presenter.State == OverlappedPresenterState.Maximized
            ? GlyphRestore
            : GlyphMaximize;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        _presenter.Minimize();

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_presenter.State == OverlappedPresenterState.Maximized)
            _presenter.Restore();
        else
            _presenter.Maximize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();
}
