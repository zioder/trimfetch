using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TrimFetch.Helpers;
using TrimFetch.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace TrimFetch;

public sealed partial class MainWindow : Window
{
    private const double InitialContentWidth = 680;
    private const double InitialContentHeight = 64;
    private const double TopAnchorMarginDip = 48;

    private readonly GlobalHotKeyService _hotKeys = new();
    private readonly PreferencesService _preferences = new();
    private Win32WindowSubclass? _windowSubclass;
    private bool _canHideOnDeactivate;
    private bool _suppressNextActivationPresent;

    /// <summary>
    /// True when the user launched the app directly (so the overlay should reveal itself,
    /// ready for a pasted URL). False for a login/startup-task launch, which stays in the tray.
    /// </summary>
    public bool ShouldRevealOnLaunch { get; set; }

    public bool IsDependencySetupMode { get; private set; }

    public bool IsMicaActive { get; private set; }

    public IRelayCommand ShowWindowCommand { get; }

    public IRelayCommand ExitCommand { get; }

    public MainWindow()
    {
        ShowWindowCommand = new RelayCommand(ShowWindow);
        ExitCommand = new RelayCommand(ExitApplication);

        InitializeComponent();
        RootGrid.RequestedTheme = ElementTheme.Dark;

        AppIconHelper.TrySetWindowIcon(AppWindow);

        ConfigurePresenter();
        ApplyOverlayChrome();
        ResizeToContent(InitialContentWidth, InitialContentHeight);

        // The very first Activate() shows a backdrop-less, unclipped HWND. Clip it away now
        // so launch paints nothing until the page is ready to reveal (no black-box flash).
        WindowRegionHelper.Collapse(WindowNative.GetWindowHandle(this));

        RootFrame.Navigate(typeof(MainPage));
        Activated += MainWindow_Activated;
    }

    private void ConfigurePresenter()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
    }

    private void ApplyOverlayChrome()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        NativeWindowChrome.ApplyBorderlessOverlay(hwnd);
        SetMicaActive(false);
        EnsureWindowSubclass(hwnd);
        _ = ReloadActivationHotKeyAsync();
    }

    /// <summary>
    /// Active overlay backdrop. Thin acrylic blurs whatever sits directly behind the overlay
    /// (so the background shows through) and reads much lighter than Mica, which only samples
    /// the wallpaper and looks dark over other windows. DWM host backdrop stays off (it fills
    /// the full HWND as a slab and breaks card separation); HWND clip regions punch desktop
    /// holes between cards, and each card gets a light frosted veil in <see cref="OverlayGlass"/>.
    /// </summary>
    public void SetOverlayBackdrop(bool active)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        IsMicaActive = active;

        if (active)
        {
            SystemBackdrop = CreateOverlayBackdrop();
        }
        else
        {
            SystemBackdrop = null;
        }

        TransparentWindowHost.SuppressHostBackdrop(hwnd);
    }

    private SystemBackdrop? CreateOverlayBackdrop()
    {
        try
        {
            if (SystemBackdrop is OverlayAcrylicBackdrop existing)
            {
                return existing;
            }

            // Custom acrylic tuned lighter/thinner than the system default so the content
            // behind the overlay shows through instead of going dark. See OverlayAcrylicBackdrop.
            return new OverlayAcrylicBackdrop();
        }
        catch
        {
            return new DesktopAcrylicBackdrop();
        }
    }

    public void SetMicaActive(bool active)
    {
        SetOverlayBackdrop(active);

        if (active && RootFrame.Content is MainPage page)
        {
            page.OnMicaStateChanged(true);
            page.ScheduleRegionUpdate();
        }
    }

    public void HideOverlay()
    {
        if (RootFrame.Content is MainPage page)
        {
            page.PrepareOverlayBeforeHide();
        }

        AppWindow.Hide();

        // Keep the acrylic backdrop alive while hidden so the next reveal renders glass
        // instantly. Recreating it on every show is what made the reveal start from black.
        // (Dependency-setup mode disables it explicitly when it needs the opaque card.)
    }

    public void NotifyLayoutException(Exception ex)
    {
        if (RootFrame.Content is MainPage page)
        {
            page.OnLayoutException(ex);
        }
    }

    private void EnsureWindowSubclass(IntPtr hwnd)
    {
        _windowSubclass ??= new Win32WindowSubclass(hwnd, ProcessWindowMessage);
    }

    private IntPtr ProcessWindowMessage(uint msg, IntPtr wParam, IntPtr lParam) =>
        _hotKeys.ProcessWindowMessage(msg, wParam) ? new IntPtr(1) : IntPtr.Zero;

    public async Task ReloadActivationHotKeyAsync()
    {
        try
        {
            var shortcut = await _preferences.GetHotKeyAsync(Models.HotKeyAction.ActivateApp);
            var hwnd = WindowNative.GetWindowHandle(this);
            _hotKeys.Attach(hwnd, () => DispatcherQueue.TryEnqueue(ShowWindow));
            _hotKeys.UpdateShortcut(shortcut);
        }
        catch
        {
        }
    }

    public double GetMaxContentHeightDip()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var scale = GetDpiForWindow(hwnd) / 96.0;
            var area = GetDisplayAreaNearCursor();
            var maxPhysical = area.WorkArea.Height - (int)(24 * scale);
            return maxPhysical / scale;
        }
        catch
        {
            return 900;
        }
    }

    /// <summary>Resizes the window and returns the content height in DIPs that actually fit on screen.</summary>
    public double ResizeToContent(double contentWidth, double contentHeight, bool isSetup = false, bool anchorTopCenter = false)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var scale = GetDpiForWindow(hwnd) / 96.0;

            var width = (int)Math.Ceiling(contentWidth * scale);
            var requestedPhysical = (int)Math.Ceiling(contentHeight * scale);

            var area = GetDisplayAreaNearCursor();
            var maxHeight = area.WorkArea.Height - (int)(24 * scale);
            var minHeight = anchorTopCenter
                ? Math.Min(requestedPhysical, maxHeight)
                : (int)(72 * scale);
            var height = Math.Clamp(requestedPhysical, minHeight, maxHeight);

            AppWindow.Resize(new SizeInt32(width, height));

            if (anchorTopCenter)
            {
                PositionTopCenterOnDisplay();
            }
            else
            {
                PositionNearCursorDisplay();
            }

            if (RootFrame.Content is MainPage page)
            {
                page.ScheduleRegionUpdate();
            }

            return height / scale;
        }
        catch
        {
            return contentHeight;
        }
    }

    public void PositionTopCenterOnDisplay()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var scale = GetDpiForWindow(hwnd) / 96.0;
            var area = GetDisplayAreaNearCursor();
            var size = AppWindow.Size;
            var x = area.WorkArea.X + (area.WorkArea.Width - size.Width) / 2;
            var y = area.WorkArea.Y + (int)(TopAnchorMarginDip * scale);
            var workBottom = area.WorkArea.Y + area.WorkArea.Height;
            if (y + size.Height > workBottom)
            {
                y = workBottom - size.Height;
            }

            if (y < area.WorkArea.Y)
            {
                y = area.WorkArea.Y;
            }

            AppWindow.Move(new PointInt32(Math.Max(area.WorkArea.X, x), y));
        }
        catch
        {
        }
    }

    public void ClearClipRegion()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        WindowRegionHelper.Clear(hwnd);
    }

    public void UpdateClipRegions(IReadOnlyList<WindowRegionHelper.RegionRect> regions)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        WindowRegionHelper.Apply(hwnd, regions);
    }

    public void PositionNearCursorDisplay()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var area = GetDisplayAreaNearCursor();
            var size = AppWindow.Size;
            var x = area.WorkArea.X + (area.WorkArea.Width - size.Width) / 2;
            var y = area.WorkArea.Y + (int)(TopAnchorMarginDip * (GetDpiForWindow(hwnd) / 96.0));
            AppWindow.Move(new PointInt32(Math.Max(area.WorkArea.X, x), Math.Max(area.WorkArea.Y, y)));
        }
        catch
        {
        }
    }

    private DisplayArea GetDisplayAreaNearCursor()
    {
        try
        {
            var point = GetCursorPosition();
            foreach (var display in DisplayArea.FindAll())
            {
                if (point.X >= display.WorkArea.X
                    && point.X < display.WorkArea.X + display.WorkArea.Width
                    && point.Y >= display.WorkArea.Y
                    && point.Y < display.WorkArea.Y + display.WorkArea.Height)
                {
                    return display;
                }
            }
        }
        catch
        {
        }

        return DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (_canHideOnDeactivate)
            {
                if (RootFrame.Content is MainPage page)
                {
                    page.OnWindowDeactivated();
                }

                if (CanHideOnDeactivate())
                {
                    if (App.Window is MainWindow window)
                    {
                        window.HideOverlay();
                    }
                    else
                    {
                        AppWindow.Hide();
                    }
                }
            }

            return;
        }

        _canHideOnDeactivate = true;

        if (_suppressNextActivationPresent)
        {
            _suppressNextActivationPresent = false;
            if (RootFrame.Content is MainPage pageActive)
            {
                pageActive.FocusInputField();
            }

            return;
        }

        if (RootFrame.Content is MainPage pageActiveAfterShow)
        {
            pageActiveAfterShow.OnWindowActivated();
            pageActiveAfterShow.FocusInputField();
        }
    }

    private bool CanHideOnDeactivate()
    {
        if (IsDependencySetupMode)
        {
            return false;
        }

        // Note: an open trim (ShowVideo) no longer blocks hiding — the trim state is preserved
        // across hide/show, so clicking away simply tucks it back behind the next hotkey reveal.
        if (RootFrame.Content is MainPage { ViewModel: { } viewModel }
            && (viewModel.ShowDependencySetup
                || viewModel.IsDownloadBusy
                || viewModel.IsTrimExporting))
        {
            return false;
        }

        return true;
    }

    public void EnterDependencySetupMode()
    {
        IsDependencySetupMode = true;
        _canHideOnDeactivate = false;

        var hwnd = WindowNative.GetWindowHandle(this);
        NativeWindowForeground.BringToForeground(hwnd);

        if (RootFrame.Content is MainPage page)
        {
            page.PresentDependencySetup();
        }

        AppWindow.Show();
        Activate();
    }

    public void ExitDependencySetupMode()
    {
        IsDependencySetupMode = false;
    }

    /// <summary>
    /// Reveals the overlay on a direct user launch using the same clean frosted fade-in
    /// as the global hotkey, so the cursor lands in the URL field ready for a pasted link.
    /// </summary>
    public void RevealOverlayForLaunch() => ShowWindow();

    public void ShowFromBackground()
    {
        if (RootFrame.Content is MainPage page && page.ViewModel.ShowDependencySetup)
        {
            EnterDependencySetupMode();
            return;
        }

        ShowWindow();
    }

    public void ShowSettings()
    {
        if (RootFrame.Content is not MainPage page)
        {
            return;
        }

        if (IsDependencySetupMode || page.ViewModel.ShowDependencySetup)
        {
            AppWindow.Show();
            Activate();
            NativeWindowForeground.BringToForeground(WindowNative.GetWindowHandle(this));
        }

        page.ShowSettings();
    }

    public void ExitApplication()
    {
        AppServices.Tray.Dispose();
        ExitApp();
    }

    private void ShowWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        TransparentWindowHost.ApplyHideFromTaskbar(hwnd);
        NativeWindowForeground.BringToForeground(hwnd);

        if (RootFrame.Content is MainPage page)
        {
            page.PrepareOverlayBeforeShow();
        }

        AppWindow.Show();
        Activate();

        _canHideOnDeactivate = true;
        _suppressNextActivationPresent = true;

        if (RootFrame.Content is MainPage pageActive)
        {
            pageActive.CompleteOverlayShow(focusInput: true);
        }
    }

    private void ExitApp()
    {
        _hotKeys.Dispose();
        _windowSubclass?.Dispose();
        Application.Current.Exit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private static PointInt32 GetCursorPosition()
    {
        _ = GetCursorPos(out var point);
        return new PointInt32(point.X, point.Y);
    }
}
