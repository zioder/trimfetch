using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace TrimFetch.Helpers;

internal static class FluentWindowChrome
{
    public const double DefaultSettingsWidthDip = 640;
    public const double DefaultSettingsHeightDip = 800;

    public static void ApplyMicaBackdrop(Window window)
    {
        try
        {
            window.SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            try
            {
                window.SystemBackdrop = new DesktopAcrylicBackdrop();
            }
            catch
            {
                window.SystemBackdrop = null;
            }
        }
    }

    /// <summary>Standard settings surface — opaque title bar and acrylic/solid fill (no overlay Mica).</summary>
    public static void ApplySettingsWindowChrome(Window window, FrameworkElement titleBarHost)
    {
        try
        {
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
            window.SystemBackdrop = null;
        }

        var hwnd = WindowNative.GetWindowHandle(window);
        TransparentWindowHost.SetHostAcrylicEnabled(hwnd, enabled: window.SystemBackdrop is not null);

        ConfigureSettingsTitleBar(window, titleBarHost);
    }

    public static void ConfigureSettingsTitleBar(Window window, UIElement titleBarHost)
    {
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBarHost);

        window.AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
    }

    public static void ConfigureExtendedTitleBar(Window window, UIElement titleBarHost)
    {
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBarHost);

        var titleBar = window.AppWindow.TitleBar;
        if (!titleBar.ExtendsContentIntoTitleBar)
        {
            titleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
            return;
        }

        titleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;

        var transparent = Microsoft.UI.Colors.Transparent;
        titleBar.BackgroundColor = transparent;
        titleBar.InactiveBackgroundColor = transparent;
        titleBar.ButtonBackgroundColor = transparent;
        titleBar.ButtonInactiveBackgroundColor = transparent;
    }

    public static void SyncTitleBarHostHeight(Window window, FrameworkElement titleBarHost)
    {
        var height = window.AppWindow.TitleBar.Height;
        if (height > 0)
        {
            titleBarHost.Height = height;
        }
    }

    public static void ResizeAndCenter(Window window, double widthDip, double heightDip, Window? owner = null)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            var scale = GetDpiForWindow(hwnd) / 96.0;
            var width = (int)Math.Ceiling(widthDip * scale);
            var height = (int)Math.Ceiling(heightDip * scale);

            window.AppWindow.Resize(new SizeInt32(width, height));
            CenterOnDisplay(window, owner);
        }
        catch
        {
            // Keep default placement if Win32/DPI calls fail.
        }
    }

    public static void TrySetWindowIcon(AppWindow appWindow)
    {
        try
        {
            appWindow.SetIcon(AppBranding.AppIconIcoPath);
        }
        catch
        {
        }
    }

    public static void ConfigureSettingsPresenter(AppWindow appWindow)
    {
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }
    }

    private static void CenterOnDisplay(Window window, Window? owner)
    {
        var size = window.AppWindow.Size;
        DisplayArea area;

        if (owner is not null)
        {
            try
            {
                area = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Nearest);
            }
            catch
            {
                area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary);
            }
        }
        else
        {
            area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary);
        }

        var x = area.WorkArea.X + Math.Max(0, (area.WorkArea.Width - size.Width) / 2);
        var y = area.WorkArea.Y + Math.Max(0, (area.WorkArea.Height - size.Height) / 2);
        window.AppWindow.Move(new PointInt32(x, y));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
