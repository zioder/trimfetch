using System.Runtime.InteropServices;

namespace TrimFetch.Helpers;

/// <summary>
/// Brings a WinUI HWND to the foreground after a global hotkey or tray action.
/// </summary>
internal static class NativeWindowForeground
{
    private const int SwRestore = 9;
    private const int SwShow = 5;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    public static void BringToForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _ = ShowWindow(hwnd, SwRestore);
        _ = ShowWindow(hwnd, SwShow);

        if (SetForegroundWindow(hwnd))
        {
            return;
        }

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            _ = BringWindowToTop(hwnd);
            _ = SetForegroundWindow(hwnd);
            return;
        }

        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();

        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            _ = BringWindowToTop(hwnd);
            _ = SetForegroundWindow(hwnd);
            return;
        }

        if (AttachThreadInput(currentThread, foregroundThread, true))
        {
            try
            {
                _ = BringWindowToTop(hwnd);
                _ = SetForegroundWindow(hwnd);
            }
            finally
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
        else
        {
            _ = BringWindowToTop(hwnd);
            _ = SetForegroundWindow(hwnd);
        }
    }
}
