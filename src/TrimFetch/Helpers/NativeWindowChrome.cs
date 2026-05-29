namespace TrimFetch.Helpers;

internal static class NativeWindowChrome
{
    public static void ApplyBorderlessOverlay(IntPtr hwnd) =>
        TransparentWindowHost.ApplyBorderlessChrome(hwnd);
}
