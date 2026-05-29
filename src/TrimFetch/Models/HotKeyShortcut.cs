using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace TrimFetch.Models;

[Flags]
public enum HotKeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

public sealed record HotKeyShortcut(VirtualKey Key, HotKeyModifiers Modifiers)
{
    public bool Matches(VirtualKey key, HotKeyModifiers modifiers) =>
        Key == key && Modifiers == modifiers;

    public bool Matches(KeyRoutedEventArgs args)
    {
        if (args.Key != Key)
        {
            return false;
        }

        return Modifiers == ReadCurrentModifiers();
    }

    public string DisplayText
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(HotKeyModifiers.Control))
            {
                parts.Add("Ctrl");
            }

            if (Modifiers.HasFlag(HotKeyModifiers.Alt))
            {
                parts.Add("Alt");
            }

            if (Modifiers.HasFlag(HotKeyModifiers.Shift))
            {
                parts.Add("Shift");
            }

            if (Modifiers.HasFlag(HotKeyModifiers.Windows))
            {
                parts.Add("Win");
            }

            parts.Add(GetKeyDisplayName(Key));
            return string.Join("+", parts);
        }
    }

    public uint RegisterHotKeyModifiers => (uint)(
        (Modifiers.HasFlag(HotKeyModifiers.Alt) ? 0x0001 : 0)
        | (Modifiers.HasFlag(HotKeyModifiers.Control) ? 0x0002 : 0)
        | (Modifiers.HasFlag(HotKeyModifiers.Shift) ? 0x0004 : 0)
        | (Modifiers.HasFlag(HotKeyModifiers.Windows) ? 0x0008 : 0));

    private static HotKeyModifiers ReadCurrentModifiers()
    {
        var mods = HotKeyModifiers.None;
        if (IsKeyDown(VirtualKey.Menu))
        {
            mods |= HotKeyModifiers.Alt;
        }

        if (IsKeyDown(VirtualKey.Control))
        {
            mods |= HotKeyModifiers.Control;
        }

        if (IsKeyDown(VirtualKey.Shift))
        {
            mods |= HotKeyModifiers.Shift;
        }

        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows))
        {
            mods |= HotKeyModifiers.Windows;
        }

        return mods;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static string GetKeyDisplayName(VirtualKey key) => key switch
    {
        VirtualKey.Enter => "Enter",
        VirtualKey.Space => "Space",
        VirtualKey.Tab => "Tab",
        VirtualKey.Escape => "Esc",
        VirtualKey.Delete => "Delete",
        VirtualKey.Back => "Backspace",
        >= VirtualKey.A and <= VirtualKey.Z => ((char)('A' + (key - VirtualKey.A))).ToString(),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + (key - VirtualKey.Number0))).ToString(),
        >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => ((char)('0' + (key - VirtualKey.NumberPad0))).ToString(),
        _ => key.ToString(),
    };
}
