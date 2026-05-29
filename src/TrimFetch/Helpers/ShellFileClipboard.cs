using System.Runtime.InteropServices;
using System.Text;

namespace TrimFetch.Helpers;

/// <summary>
/// Win32 CF_HDROP clipboard copy for Explorer paste (files).
/// </summary>
internal static class ShellFileClipboard
{
    private const uint CfHdrop = 15;
    private const uint GmemMoveable = 0x0002;

    // Win32 DROPFILES header is always 20 bytes before the path list.
    private const int DropfilesHeaderSize = 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll")]
    private static extern uint GetLastError();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr handle);

    public static void CopyFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            throw new InvalidOperationException("No files to copy.");
        }

        var normalized = paths
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new FileNotFoundException("Files to copy were not found.");
        }

        var pathsBlob = string.Join('\0', normalized) + "\0\0";
        var pathsBytes = Encoding.Unicode.GetBytes(pathsBlob);
        var totalSize = DropfilesHeaderSize + pathsBytes.Length;

        var global = GlobalAlloc(GmemMoveable, (UIntPtr)totalSize);
        if (global == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Clipboard alloc failed ({GetLastError()}).");
        }

        var locked = GlobalLock(global);
        if (locked == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Clipboard lock failed ({GetLastError()}).");
        }

        try
        {
            WriteDropfilesHeader(locked);
            Marshal.Copy(pathsBytes, 0, locked + DropfilesHeaderSize, pathsBytes.Length);
        }
        finally
        {
            GlobalUnlock(global);
        }

        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (TrySetClipboard(global))
            {
                return;
            }

            Thread.Sleep(40 * (attempt + 1));
        }

        GlobalFree(global);
        throw new InvalidOperationException($"Could not open or write the clipboard ({GetLastError()}).");
    }

    private static void WriteDropfilesHeader(IntPtr basePtr)
    {
        Marshal.WriteInt32(basePtr, 0, DropfilesHeaderSize);
        Marshal.WriteInt32(basePtr, 4, 0);
        Marshal.WriteInt32(basePtr, 8, 0);
        Marshal.WriteInt32(basePtr, 12, 0);
        Marshal.WriteInt32(basePtr, 16, 1);
    }

    private static bool TrySetClipboard(IntPtr global)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            return SetClipboardData(CfHdrop, global) != IntPtr.Zero;
        }
        finally
        {
            CloseClipboard();
        }
    }
}
