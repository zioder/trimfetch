using System.Runtime.InteropServices;
using TrimFetch.Models;
using Windows.System;

namespace TrimFetch.Services;

public sealed class GlobalHotKeyService : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const int ActivationHotKeyId = 1;

    private IntPtr _hwnd;
    private HotKeyShortcut _shortcut = HotKeyAction.ActivateApp.GetDefaultShortcut();
    private Action? _onActivate;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void Attach(IntPtr hwnd, Action onActivate)
    {
        _hwnd = hwnd;
        _onActivate = onActivate;
        RegisterCurrent();
    }

    public void UpdateShortcut(HotKeyShortcut shortcut)
    {
        _shortcut = shortcut;
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, ActivationHotKeyId);
            RegisterCurrent();
        }
    }

    public bool ProcessWindowMessage(uint msg, IntPtr wParam)
    {
        if (msg == WmHotKey && wParam.ToInt32() == ActivationHotKeyId)
        {
            _onActivate?.Invoke();
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, ActivationHotKeyId);
        }
    }

    private void RegisterCurrent()
    {
        if (_hwnd == IntPtr.Zero || _shortcut.Key == VirtualKey.None)
        {
            return;
        }

        _ = RegisterHotKey(
            _hwnd,
            ActivationHotKeyId,
            _shortcut.RegisterHotKeyModifiers,
            (uint)_shortcut.Key);
    }
}
