using System.Runtime.InteropServices;

namespace TrimFetch.Helpers;

internal sealed class Win32WindowSubclass : IDisposable
{
    private readonly SUBCLASSPROC _callback;
    private readonly Func<uint, IntPtr, IntPtr, IntPtr> _handler;
    private readonly IntPtr _hwnd;
    private bool _disposed;

    public Win32WindowSubclass(IntPtr hwnd, Func<uint, IntPtr, IntPtr, IntPtr> handler)
    {
        _hwnd = hwnd;
        _handler = handler;
        _callback = SubclassProc;
        _ = SetWindowSubclass(hwnd, _callback, UIntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _ = RemoveWindowSubclass(_hwnd, _callback, UIntPtr.Zero);
        _disposed = true;
    }

    private IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        var handled = _handler(uMsg, wParam, lParam);
        return handled != IntPtr.Zero
            ? handled
            : DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private delegate IntPtr SUBCLASSPROC(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SUBCLASSPROC pfnSubclass,
        UIntPtr uIdSubclass,
        IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SUBCLASSPROC pfnSubclass,
        UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
