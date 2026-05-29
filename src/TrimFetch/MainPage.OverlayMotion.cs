using Microsoft.UI.Xaml;

namespace TrimFetch;

/// <summary>Input and history entrance animations. Trim uses <see cref="MainPage.ScheduleOpenTrimSurface"/>.</summary>
public sealed partial class MainPage
{
    private bool _hasCompletedInitialEntrance;
    private bool _isInputEntranceActive;
    private bool _isHistoryEntranceActive;

    private bool IsOverlayContentVisible() =>
        _hasCompletedInitialEntrance
        && !ViewModel.ShowDependencySetup
        && InputChromeHost.Opacity > 0.99;

    public void RunActivationAnimation(bool replay = false)
    {
        if (ViewModel.ShowDependencySetup)
        {
            return;
        }

        if (!replay && (_isInputEntranceActive || IsOverlayContentVisible()))
        {
            return;
        }

        PlayOverlayEntranceAnimation();
    }

    private void PlayOverlayEntranceAnimation()
    {
        InputEntranceStoryboard.Stop();
        TrimEntranceStoryboard.Stop();
        HistoryEntranceStoryboard.Stop();

        _isInputEntranceActive = true;
        _isHistoryEntranceActive = false;

        BeginInputEntrance();

        if (ViewModel.HasHistory)
        {
            BeginHistoryEntrance();
        }
        else
        {
            SyncHistorySection();
        }

        FocusInputField();
        UpdateWindowClipRegion();
    }

    private void BeginInputEntrance()
    {
        InputEntranceStoryboard.Stop();
        InputChromeHost.Opacity = 0;
        InputTransform.TranslateY = 0;
        InputEntranceStoryboard.Begin();
    }

    private void BeginHistoryEntrance()
    {
        if (!ViewModel.HasHistory)
        {
            return;
        }

        _isHistoryEntranceActive = true;
        HistoryEntranceStoryboard.Stop();
        HistoryPanel.Opacity = 0;
        HistoryTransform.TranslateY = 0;
        HistoryEntranceStoryboard.Begin();
    }

    private void OnInputEntranceCompleted(object? sender, object e)
    {
        _isInputEntranceActive = false;
        if (!IsEntranceAnimating)
        {
            OnOverlayEntranceFinished();
        }

        UpdateWindowClipRegion();
    }

    private void OnHistoryEntranceCompleted(object? sender, object e)
    {
        _isHistoryEntranceActive = false;
        OnOverlayEntranceFinished();
        UpdateWindowClipRegion();
    }

    private void EnsureOverlayContentVisible()
    {
        InputEntranceStoryboard.Stop();
        TrimEntranceStoryboard.Stop();
        HistoryEntranceStoryboard.Stop();
        _isInputEntranceActive = false;
        _isHistoryEntranceActive = false;

        ContentStack.Opacity = 1;
        InputChromeHost.Opacity = 1;
        InputTransform.TranslateY = 0;

        if (TrimPanel.Visibility == Visibility.Visible)
        {
            TrimPanel.Opacity = 1;
            TrimTransform.TranslateY = 0;
        }

        SyncHistorySection();
    }

    private void ResetSectionVisualStates() => EnsureOverlayContentVisible();

    private void SyncHistorySection()
    {
        if (!ViewModel.HasHistory)
        {
            return;
        }

        HistoryEntranceStoryboard.Stop();
        _isHistoryEntranceActive = false;
        HistoryPanel.Opacity = 1;
        HistoryTransform.TranslateY = 0;
        RequestOverlayBoundsUpdate(remeasure: true);
    }

    private void HandleOverlaySectionsChanged()
    {
        if (_revealTrimForDownloadCompletion || ViewModel.DownloadTrimReadyForReveal)
        {
            HistoryEntranceStoryboard.Stop();
            HistoryPanel.Opacity = 1;
            HistoryTransform.TranslateY = 0;
            SyncOverlayLayout();
            return;
        }

        if (ViewModel.HasHistory && !_isInputEntranceActive && !_isHistoryEntranceActive)
        {
            SyncHistorySection();
        }

        UpdateInputThumbnailChrome();
        SyncOverlayLayout();
    }
}
