using Windows.System;

namespace TrimFetch.Models;

public enum HotKeyAction
{
    Copy,
    CopyGif,
    CopyHistoryGif,
    OpenTrim,
    ActivateApp,
}

public static class HotKeyActionExtensions
{
    public static string GetTitle(this HotKeyAction action) => action switch
    {
        HotKeyAction.Copy => "Copy",
        HotKeyAction.CopyGif => "Copy trim as GIF",
        HotKeyAction.CopyHistoryGif => "Copy as GIF",
        HotKeyAction.OpenTrim => "Open trim mode",
        HotKeyAction.ActivateApp => "Activate app",
        _ => action.ToString(),
    };

    public static HotKeyShortcut GetDefaultShortcut(this HotKeyAction action) => action switch
    {
        HotKeyAction.Copy => new HotKeyShortcut(VirtualKey.Enter, HotKeyModifiers.None),
        HotKeyAction.CopyGif => new HotKeyShortcut(VirtualKey.G, HotKeyModifiers.Control),
        HotKeyAction.CopyHistoryGif => new HotKeyShortcut(VirtualKey.G, HotKeyModifiers.Control | HotKeyModifiers.Shift),
        HotKeyAction.OpenTrim => new HotKeyShortcut(VirtualKey.Enter, HotKeyModifiers.Control),
        HotKeyAction.ActivateApp => new HotKeyShortcut(VirtualKey.W, HotKeyModifiers.Control | HotKeyModifiers.Shift),
        _ => new HotKeyShortcut(VirtualKey.None, HotKeyModifiers.None),
    };
}
