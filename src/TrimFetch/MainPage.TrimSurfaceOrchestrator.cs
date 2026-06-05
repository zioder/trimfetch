using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using TrimFetch.Helpers;
using TrimFetch.Models;
using Windows.Foundation;
using Windows.Media.Core;

namespace TrimFetch;

/// <summary>Trim surface: one queued operation at a time, one layout sync per transition.</summary>
public sealed partial class MainPage
{
    private int _trimSession;
    private string? _trimOpenItemId;
    private TaskCompletionSource? _trimSurfaceReady;
    private int _trimSurfaceQueued;
    private TaskCompletionSource<bool>? _trimMediaReadyTcs;

    /// <summary>While opening trim after a download, map media-wait time onto the download contour.</summary>
    private bool _revealTrimForDownloadCompletion;

    /// <summary>Do not grow the overlay for trim until the download bar has reached 100%.</summary>
    private bool _excludeTrimFromOverlayHeight;

    /// <summary>Blocks <see cref="RevealVideo"/> until the download contour is full.</summary>
    private bool _blockTrimVideoReveal;

    /// <summary>Reserve trim height in overlay measure while the panel stays collapsed for prep.</summary>
    private bool _includeTrimHeightInOverlayMeasure;

    /// <summary>Download completion is replacing an already-open trim item instead of revealing trim from hidden.</summary>
    private bool _downloadPrepUsesOpenTrimSwap;

    private Storyboard? _downloadHistoryMoveStoryboard;

    private void ScheduleOpenTrimSurface()
    {
        _trimSurfaceReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (Interlocked.Exchange(ref _trimSurfaceQueued, 1) == 1)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                Interlocked.Exchange(ref _trimSurfaceQueued, 0);
                var session = Interlocked.Increment(ref _trimSession);
                await RunTrimSurfaceOperationAsync(session);
            }
            finally
            {
                SignalTrimSurfaceReady();
            }
        });
    }

    private void SignalTrimSurfaceReady()
    {
        _trimSurfaceReady?.TrySetResult();
        _trimSurfaceReady = null;
    }

    private async Task RunTrimSurfaceOperationAsync(int session)
    {
        var item = ViewModel.ActiveTrimItem;
        if (item is null)
        {
            if (session == _trimSession)
            {
                CloseTrimUi();
            }

            return;
        }

        if (item.Id == _trimOpenItemId
            && TrimPanel.Visibility == Visibility.Visible
            && ViewModel.IsTrimMediaReady)
        {
            return;
        }

        var swapInPlace = TrimPanel.Visibility == Visibility.Visible
            && _trimOpenItemId is not null
            && item.Id != _trimOpenItemId;

        if (swapInPlace)
        {
            await SwapTrimInPlaceAsync(session, item);
        }
        else
        {
            await OpenTrimFirstShowAsync(session, item);
        }
    }

    private async Task OpenTrimFirstShowAsync(int session, DownloadItem item)
    {
        if (!await PrepareTrimSurfaceCoreAsync(session, item, CancellationToken.None))
        {
            return;
        }

        if (session != _trimSession)
        {
            return;
        }

        if (_revealTrimForDownloadCompletion)
        {
            return;
        }

        await RevealTrimSurfaceAsync(session, item);
    }

    private async Task RevealTrimSurfaceAsync(int session, DownloadItem item)
    {
        CompleteTrimOpen(session, item);
        await PlayTrimEntranceAsync(session);
    }

    /// <summary>
    /// Video is ready: smoothly complete the progress ring (held at <c>PostProcessEnd</c> during
    /// the hidden trim prep) instead of snapping to 100%, then reveal the trim surface in its
    /// final state while the ring dissolves and history slides into its new slot. The panel is
    /// shown opaque in a single layout pass — its Mica card chrome is drawn from the window clip
    /// region, so a fade-in would expose the bare backdrop as an empty card.
    /// </summary>
    private async Task PlayDownloadCompletionRevealAsync(int session, DownloadItem item)
    {
        if (session != _trimSession)
        {
            return;
        }

        // Prep is done: stop the creep and smoothly fill the rest to 100%. IsFileDownloading
        // stays true here so the contour remains visible while it fills.
        StopTrimPrepCreep();
        await AnimateContourToProgressAsync(1, TimeSpan.FromMilliseconds(220));
        if (session != _trimSession)
        {
            return;
        }

        ViewModel.ReportPipelineProgress(1);

        TrimEntranceStoryboard.Stop();
        HistoryEntranceStoryboard.Stop();
        _downloadHistoryMoveStoryboard?.Stop();

        ContentStack.UpdateLayout();
        var historyStartTop = TryGetElementTop(HistoryPanel);

        // Mark the fade-out before clearing IsFileDownloading so the chrome handler doesn't
        // snap-collapse the contour mid-reveal.
        BeginDownloadContourFadeOut();
        ViewModel.CompleteDownloadPipelineVisuals();

        _excludeTrimFromOverlayHeight = false;
        _includeTrimHeightInOverlayMeasure = false;
        _blockTrimVideoReveal = false;

        SetTrimPanelVisible(opacity: 1);
        CompleteTrimOpen(session, item);
        TrimTimeline.RefreshLayout();

        // Video is now on the trim surface: add the download to history so it slides into the
        // list (as an opaque block) after the video appears, rather than being pre-listed.
        EnsureDownloadInHistory(item);

        InvalidateOverlaySize();
        SyncOverlayLayout();
        ContentStack.UpdateLayout();

        var historyEndTop = TryGetElementTop(HistoryPanel);
        var historyDelta = historyStartTop.HasValue && historyEndTop.HasValue
            ? historyStartTop.Value - historyEndTop.Value
            : 0;

        await PlayHistoryTakePlaceAsync(historyDelta, session);

        _includeTrimHeightInOverlayMeasure = false;
        FinishDownloadContourFadeOut();
    }

    private async Task PlayDownloadCompletionSwapAsync(int session, DownloadItem item)
    {
        if (session != _trimSession)
        {
            return;
        }

        StopTrimPrepCreep();
        await AnimateContourToProgressAsync(1, TimeSpan.FromMilliseconds(180));
        if (session != _trimSession)
        {
            return;
        }

        ViewModel.ReportPipelineProgress(1);

        HistoryEntranceStoryboard.Stop();
        _downloadHistoryMoveStoryboard?.Stop();

        ContentStack.UpdateLayout();
        var historyStartTop = TryGetElementTop(HistoryPanel);

        BeginDownloadContourFadeOut();
        ViewModel.CompleteDownloadPipelineVisuals();

        EnsureDownloadInHistory(item);
        SyncOverlayLayout();
        ContentStack.UpdateLayout();

        var historyEndTop = TryGetElementTop(HistoryPanel);
        var historyDelta = historyStartTop.HasValue && historyEndTop.HasValue
            ? historyStartTop.Value - historyEndTop.Value
            : 0;

        await PlayHistoryTakePlaceAsync(historyDelta, session);

        FinishDownloadContourFadeOut();
    }

    private void SyncDownloadPrepOverlayBounds(int session)
    {
        if (session != _trimSession)
        {
            return;
        }

        _includeTrimHeightInOverlayMeasure = true;
        _excludeTrimFromOverlayHeight = false;
        InvalidateOverlaySize();
        SyncOverlayLayout();
        ContentStack.UpdateLayout();
    }

    private async Task PrewarmTrimVideoForRevealAsync(int session)
    {
        if (session != _trimSession
            || ViewModel.IsAudioMode
            || VideoPlayer.MediaPlayer is not { } player)
        {
            return;
        }

        if (!HasValidMediaDuration())
        {
            return;
        }

        try
        {
            player.PlaybackSession.Position = TimeSpan.Zero;
            player.Play();
            await Task.Delay(48);
            if (session != _trimSession)
            {
                return;
            }

            player.Pause();
            player.PlaybackSession.Position = TimeSpan.Zero;
        }
        catch
        {
            // Prep still succeeds; reveal may decode on first paint.
        }
    }

    private double? TryGetElementTop(FrameworkElement element)
    {
        if (element.Visibility != Visibility.Visible || element.ActualHeight <= 0)
        {
            return null;
        }

        try
        {
            return element
                .TransformToVisual(PageRoot)
                .TransformPoint(new Point(0, 0))
                .Y;
        }
        catch
        {
            return null;
        }
    }

    private async Task PlayHistoryTakePlaceAsync(double deltaY, int session)
    {
        if (session != _trimSession || !ViewModel.HasHistory)
        {
            return;
        }

        HistoryEntranceStoryboard.Stop();
        _downloadHistoryMoveStoryboard?.Stop();

        HistoryPanel.Opacity = 1;

        if (Math.Abs(deltaY) < 0.5)
        {
            HistoryTransform.TranslateY = 0;
            UpdateWindowClipRegion();
            return;
        }

        HistoryTransform.TranslateY = deltaY;
        UpdateWindowClipRegion();

        await YieldUiDispatcherAsync();

        if (session != _trimSession)
        {
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var storyboard = new Storyboard();
        var move = new DoubleAnimation
        {
            From = deltaY,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseOut,
            },
        };

        Storyboard.SetTarget(move, HistoryTransform);
        Storyboard.SetTargetProperty(move, "TranslateY");

        storyboard.Children.Add(move);
        _downloadHistoryMoveStoryboard = storyboard;

        void OnCompleted(object? sender, object e)
        {
            storyboard.Completed -= OnCompleted;

            if (ReferenceEquals(_downloadHistoryMoveStoryboard, storyboard))
            {
                _downloadHistoryMoveStoryboard = null;
            }

            if (session == _trimSession)
            {
                HistoryPanel.Opacity = 1;
                HistoryTransform.TranslateY = 0;
                UpdateWindowClipRegion();
            }

            tcs.TrySetResult();
        }

        storyboard.Completed += OnCompleted;
        storyboard.Begin();

        await tcs.Task;
    }

    private bool IsDownloadTrimPrimedForReveal(DownloadItem item, int session)
    {
        if (session != _trimSession
            || _trimOpenItemId is null
            || !string.Equals(_trimOpenItemId, item.Id, StringComparison.Ordinal))
        {
            return false;
        }

        if (ViewModel.IsAudioMode)
        {
            return ViewModel.MediaDurationSeconds > 0;
        }

        return HasValidMediaDuration();
    }

    private async Task<bool> PrepareTrimSurfaceCoreAsync(
        int session,
        DownloadItem item,
        CancellationToken cancellationToken)
    {
        _trimOpenItemId = item.Id;
        ViewModel.IsTrimMediaReady = false;
        StopTrimPlayback();
        HideTrimPanel();

        if (!PreparePoster(item))
        {
            AbortTrimOpen(session);
            return false;
        }

        await ViewModel.EnsureTrimMediaProfileAsync(item);
        if (session != _trimSession || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        SyncTrimModeUiFromViewModel();

        if (!AttachVideo(item, session))
        {
            AbortTrimOpen(session);
            return false;
        }

        _ = ViewModel.TryApplyCachedTimelineStrip(item);

        if (_revealTrimForDownloadCompletion)
        {
            PrimeTrimPanelHiddenForDownloadReveal();
        }
        else
        {
            SetTrimPanelVisible(opacity: 0);
            TrimTimeline.RefreshLayout();
            SyncOverlayLayout();
        }

        if (_revealTrimForDownloadCompletion)
        {
            if (!await WaitForTrimMediaReadyWithPrepProgressAsync(
                    session,
                    item,
                    TimeSpan.FromSeconds(8),
                    cancellationToken))
            {
                AbortTrimOpen(session);
                return false;
            }
        }
        else if (!await WaitForTrimMediaReadyAsync(session, item, TimeSpan.FromSeconds(6)))
        {
            AbortTrimOpen(session);
            return false;
        }

        return session == _trimSession && !cancellationToken.IsCancellationRequested;
    }

    private async Task SwapTrimInPlaceAsync(int session, DownloadItem item)
    {
        _trimOpenItemId = item.Id;
        ViewModel.IsTrimMediaReady = false;
        StopTrimPlayback();

        if (!PreparePoster(item))
        {
            AbortTrimOpen(session);
            return;
        }

        await ViewModel.EnsureTrimMediaProfileAsync(item);
        if (session != _trimSession)
        {
            return;
        }

        SyncTrimModeUiFromViewModel();

        if (!AttachVideo(item, session))
        {
            AbortTrimOpen(session);
            return;
        }

        _ = ViewModel.TryApplyCachedTimelineStrip(item);
        SetTrimPanelVisible(opacity: 1);
        TrimTimeline.RefreshLayout();
        SyncOverlayLayout();

        if (!await WaitForTrimMediaReadyAsync(session, item, TimeSpan.FromSeconds(6)))
        {
            AbortTrimOpen(session);
            return;
        }

        if (session != _trimSession)
        {
            return;
        }

        CompleteTrimOpen(session, item);
    }

    private void CompleteTrimOpen(int session, DownloadItem item)
    {
        if (session != _trimSession)
        {
            return;
        }

        ViewModel.DismissInputPreviewThumbnail();

        if (_blockTrimVideoReveal)
        {
            return;
        }

        ViewModel.IsTrimMediaReady = true;
        RevealVideo();
        StartAutoplay();
        if (ViewModel.IsAudioMode)
        {
            _ = ViewModel.EnsureWaveformPeaksAsync(item);
        }
        else
        {
            _ = ViewModel.EnsureTimelineStripAsync(item);
        }
    }

    private async Task PlayTrimEntranceAsync(int session)
    {
        if (session != _trimSession)
        {
            return;
        }

        TrimEntranceStoryboard.Stop();
        TrimTransform.TranslateY = 0;
        TrimPanel.Opacity = 0;
        ContentStack.UpdateLayout();
        TrimPanel.UpdateLayout();
        await YieldUiDispatcherAsync();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(object? sender, object e)
        {
            TrimEntranceStoryboard.Completed -= OnCompleted;
            if (session != _trimSession)
            {
                tcs.TrySetResult();
                return;
            }

            TrimPanel.Opacity = 1;
            TrimTransform.TranslateY = 0;
            UpdateWindowClipRegion();
            tcs.TrySetResult();
        }

        TrimEntranceStoryboard.Completed += OnCompleted;
        TrimEntranceStoryboard.Begin();
        await tcs.Task;
    }

    private void SetTrimPanelVisible(double opacity)
    {
        TrimEntranceStoryboard.Stop();
        TrimPanel.Visibility = Visibility.Visible;
        TrimPanel.Opacity = opacity;
        TrimTransform.TranslateY = 0;
    }

    private void PrimeTrimPanelHiddenForDownloadReveal()
    {
        TrimEntranceStoryboard.Stop();

        TrimPanel.Visibility = Visibility.Collapsed;
        TrimPanel.Opacity = 0;
        TrimTransform.TranslateY = 0;

        VideoPlayer.Visibility = Visibility.Collapsed;
        TrimPosterImage.Visibility = Visibility.Collapsed;
        AudioModePlaceholder.Visibility = Visibility.Collapsed;
    }

    private void AbortTrimOpen(int session)
    {
        if (session != _trimSession)
        {
            return;
        }

        StopTrimPlayback();
        VideoPlayer.Source = null;
        _trimMediaReadyTcs = null;
        HideTrimPanel();
        ViewModel.IsTrimMediaReady = false;
        _trimOpenItemId = null;
        SyncOverlayLayout();
    }

    private void CloseTrimUi()
    {
        Interlocked.Increment(ref _trimSession);
        _trimOpenItemId = null;
        StopTrimPlayback();
        VideoPlayer.Source = null;
        _trimMediaReadyTcs = null;
        TrimPosterImage.Source = null;
        HideTrimPanel();
        ViewModel.IsTrimMediaReady = false;
        SyncOverlayLayout();
    }

    private void HideTrimPanel()
    {
        TrimEntranceStoryboard.Stop();
        TrimPanel.Visibility = Visibility.Collapsed;
        TrimPanel.Opacity = 0;
        TrimTransform.TranslateY = 0;
        VideoPlayer.Visibility = Visibility.Collapsed;
        TrimPosterImage.Visibility = Visibility.Collapsed;
        AudioModePlaceholder.Visibility = Visibility.Collapsed;
        SetTrimAudioMode(audio: false);
    }

    private bool PreparePoster(DownloadItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ThumbnailPath) || !File.Exists(item.ThumbnailPath))
        {
            TrimPosterImage.Source = null;
            TrimPosterImage.Visibility = Visibility.Collapsed;
            return true;
        }

        if (TrySetPoster(item))
        {
            return true;
        }

        var bitmap = ImageFileSource.FromPath(item.ThumbnailPath, 960);
        if (bitmap is null)
        {
            return true;
        }

        TrimPosterImage.Source = bitmap;
        TrimPosterImage.Visibility = ImageFileSource.IsDecoded(bitmap) ? Visibility.Visible : Visibility.Collapsed;
        return true;
    }

    private bool TrySetPoster(DownloadItem item)
    {
        var path = Path.GetFullPath(item.ThumbnailPath!);
        if (InputThumbnailImage.Source is BitmapImage warm
            && ImageFileSource.IsDecoded(warm)
            && ViewModel.InputPreviewThumbnailPath is { } preview
            && string.Equals(Path.GetFullPath(preview), path, StringComparison.OrdinalIgnoreCase))
        {
            TrimPosterImage.Source = warm;
            TrimPosterImage.Visibility = Visibility.Visible;
            return true;
        }

        var bitmap = ImageFileSource.FromPath(item.ThumbnailPath, 960);
        if (bitmap is null || !ImageFileSource.IsDecoded(bitmap))
        {
            return false;
        }

        TrimPosterImage.Source = bitmap;
        TrimPosterImage.Visibility = Visibility.Visible;
        return true;
    }

    private bool AttachVideo(DownloadItem item, int session)
    {
        if (session != _trimSession)
        {
            return false;
        }

        if (ViewModel.TrimMediaProfile == TrimMediaProfile.AudioOnly)
        {
            VideoPlayer.Source = null;
            VideoPlayer.Visibility = Visibility.Collapsed;
            return true;
        }

        var uri = ViewModel.ActiveTrimFileUri;
        if (uri is null)
        {
            return false;
        }

        _trimMediaReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoPlayer.Visibility = Visibility.Collapsed;
        VideoPlayer.Source = MediaSource.CreateFromUri(uri);
        return true;
    }

    private async Task<bool> WaitForTrimMediaReadyAsync(int session, DownloadItem item, TimeSpan timeout)
    {
        if (ViewModel.TrimMediaProfile == TrimMediaProfile.AudioOnly)
        {
            return await ViewModel.EnsureTrimDurationFromProbeAsync(item)
                && session == _trimSession;
        }

        return await WaitForMediaReadyAsync(session, timeout);
    }

    private async Task<bool> WaitForTrimMediaReadyWithPrepProgressAsync(
        int session,
        DownloadItem item,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;

        while (DateTime.UtcNow - started < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session != _trimSession)
            {
                return false;
            }

            if (ViewModel.TrimMediaProfile == TrimMediaProfile.AudioOnly)
            {
                if (await ViewModel.EnsureTrimDurationFromProbeAsync(item))
                {
                    return session == _trimSession;
                }
            }
            else if (HasValidMediaDuration())
            {
                return true;
            }

            await Task.Delay(33);
        }

        return ViewModel.TrimMediaProfile == TrimMediaProfile.AudioOnly
            ? await ViewModel.EnsureTrimDurationFromProbeAsync(item) && session == _trimSession
            : HasValidMediaDuration() && session == _trimSession;
    }

    private async Task<bool> WaitForMediaReadyAsync(int session, TimeSpan timeout)
    {
        if (HasValidMediaDuration())
        {
            return session == _trimSession;
        }

        var mediaSignal = _trimMediaReadyTcs?.Task ?? Task.FromResult(false);
        var completed = await Task.WhenAny(mediaSignal, Task.Delay(timeout));
        if (session != _trimSession)
        {
            return false;
        }

        if (completed == mediaSignal)
        {
            try
            {
                if (await mediaSignal)
                {
                    await Task.Delay(32);
                    return HasValidMediaDuration() && session == _trimSession;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return HasValidMediaDuration() && session == _trimSession;
    }

    private bool HasValidMediaDuration()
    {
        var duration = VideoPlayer.MediaPlayer?.PlaybackSession.NaturalDuration.TotalSeconds ?? 0;
        return double.IsFinite(duration) && duration > 0;
    }

    private void SyncTrimModeUiFromViewModel() =>
        SetTrimAudioMode(ViewModel.IsAudioMode);

    private void OnTrimMediaFailed(int session)
    {
        if (session != _trimSession)
        {
            return;
        }

        AbortTrimOpen(session);
    }

    private void RevealVideo()
    {
        if (ViewModel.IsAudioMode)
        {
            TrimPosterImage.Visibility = Visibility.Collapsed;
            VideoPlayer.Visibility = Visibility.Collapsed;
            AudioModePlaceholder.Visibility = Visibility.Visible;
            return;
        }

        AudioModePlaceholder.Visibility = Visibility.Collapsed;
        TrimPosterImage.Visibility = Visibility.Collapsed;
        VideoPlayer.Visibility = Visibility.Visible;
    }

    private void EnsureTrimVideoVisible()
    {
        if (!ViewModel.IsTrimMediaReady)
        {
            return;
        }

        if (ViewModel.IsAudioMode)
        {
            ApplyTrimPreviewSurface(audio: true);
            return;
        }

        RevealVideo();
    }

    private void StartAutoplay()
    {
        if (ViewModel.TrimMediaProfile == TrimMediaProfile.AudioOnly)
        {
            return;
        }

        if (VideoPlayer.MediaPlayer is not { } player)
        {
            return;
        }

        var (start, end) = GetTrimPlaybackRange();
        if (end <= start)
        {
            return;
        }

        player.PlaybackSession.Position = TimeSpan.FromSeconds(start);
        player.Play();
        _trimPlaybackTimer.Start();
        _isTrimPlaying = true;
        PlayPauseIcon.Glyph = "\uE769";
        UpdateTrimPlaybackPosition();
        UpdateTrimControlsVisibility();
    }

    private (double Start, double End) GetTrimPlaybackRange()
    {
        var duration = ViewModel.MediaDurationSeconds;
        if (duration <= 0 && VideoPlayer.MediaPlayer?.PlaybackSession.NaturalDuration is { } natural)
        {
            var seconds = natural.TotalSeconds;
            if (double.IsFinite(seconds) && seconds > 0)
            {
                duration = seconds;
            }
        }

        if (duration <= 0)
        {
            duration = Math.Max(ViewModel.TrimEndSeconds, 1);
        }

        var start = Math.Clamp(ViewModel.TrimStartSeconds, 0, duration);
        var end = Math.Clamp(ViewModel.TrimEndSeconds, start, duration);
        if (end <= start)
        {
            end = duration;
        }

        return (start, end);
    }
}
