using Microsoft.UI.Xaml;

namespace TrimFetch;

/// <summary>Smooth hotkey reveal: clear desktop → frosted cards fade-in.</summary>
public sealed partial class MainPage
{
    private Action? _hotkeyRevealCompletion;

    /// <summary>Reset to invisible-but-laid-out before the HWND is shown.</summary>
    public void PrepareHotkeyShowVisualState()
    {
        InputEntranceStoryboard.Stop();
        TrimEntranceStoryboard.Stop();
        HistoryEntranceStoryboard.Stop();
        HotkeyRevealStoryboard.Stop();

        _isInputEntranceActive = false;
        _isHistoryEntranceActive = false;

        InputChromeHost.Opacity = 1;
        InputTransform.TranslateY = 0;
        HistoryPanel.Opacity = 1;
        HistoryTransform.TranslateY = 0;

        if (TrimPanel.Visibility == Visibility.Visible)
        {
            TrimPanel.Opacity = 1;
            TrimTransform.TranslateY = 0;
        }

        ContentStack.Opacity = 0;
    }

    public void PrepareOverlayBeforeHide()
    {
        HotkeyRevealStoryboard.Stop();
        ContentStack.Opacity = 0;
    }

    public void PlayHotkeyRevealAnimation(Action? onCompleted = null)
    {
        HotkeyRevealStoryboard.Stop();
        ContentStack.Opacity = 0;
        _hotkeyRevealCompletion = onCompleted;
        HotkeyRevealStoryboard.Begin();
    }

    private void OnHotkeyRevealCompleted(object? sender, object e)
    {
        ContentStack.Opacity = 1;
        _hasCompletedInitialEntrance = true;

        var completion = _hotkeyRevealCompletion;
        _hotkeyRevealCompletion = null;
        completion?.Invoke();

        UpdateWindowClipRegion();
    }
}
