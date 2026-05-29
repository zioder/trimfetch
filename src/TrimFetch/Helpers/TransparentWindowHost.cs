using System.Runtime.InteropServices;

using WinUIEx;

namespace TrimFetch.Helpers;

internal static class TransparentWindowHost
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaVisibleFrameBorderThickness = 37;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwcpDonotRound = 1;
    private const int DwmsbtNone = 1;
    private const int DwmsbtMainWindow = 2;
    private const int DwmsbtTransientWindow = 3;
    private const uint DwmColorNone = 0xFFFFFFFE;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint attributeValue, int attributeSize);

    public static void ApplyBorderlessChrome(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            HwndExtensions.ToggleWindowStyle(
                hwnd,
                false,
                WindowStyle.Caption | WindowStyle.ThickFrame | WindowStyle.Border | WindowStyle.DlgFrame | WindowStyle.SysMenu);

            var cornerPreference = DwmwcpDonotRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));

            var borderColor = DwmColorNone;
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(uint));

            var borderThickness = 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaVisibleFrameBorderThickness, ref borderThickness, sizeof(int));

            SuppressHostBackdrop(hwnd);
            ApplyHideFromTaskbar(hwnd);
        }
        catch
        {
            // DWM calls can fail on some builds.
        }
    }

    private const int GwlExStyle = -20;
    private const nint WsExToolWindow = 0x00000080;
    private const nint WsExAppWindow = 0x00040000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr(IntPtr hWnd, int nIndex, nint dwNewLong);

    /// <summary>
    /// Keeps the overlay off the taskbar and Alt+Tab (launcher-style, tray-only presence).
    /// </summary>
    public static void ApplyHideFromTaskbar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
            exStyle |= WsExToolWindow;
            exStyle &= ~WsExAppWindow;
            _ = SetWindowLongPtr(hwnd, GwlExStyle, exStyle);
        }
        catch
        {
        }
    }

    public static void SetHostMicaEnabled(IntPtr hwnd, bool enabled) =>
        SetHostBackdropType(hwnd, enabled ? DwmsbtMainWindow : DwmsbtNone);

    public static void SetHostAcrylicEnabled(IntPtr hwnd, bool enabled) =>
        SetHostBackdropType(hwnd, enabled ? DwmsbtTransientWindow : DwmsbtNone);

    /// <summary>
    /// Disables DWM system backdrop on the HWND.
    /// </summary>
    public static void SuppressHostBackdrop(IntPtr hwnd) => SetHostBackdropType(hwnd, DwmsbtNone);

    private static void SetHostBackdropType(IntPtr hwnd, int backdropType)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int));
        }
        catch
        {
            // Optional on older builds.
        }
    }
}
