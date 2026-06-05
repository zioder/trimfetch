using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Shapes;
using TrimFetch.Helpers;

namespace TrimFetch;

public sealed partial class MainPage
{
    private const double ContourCornerRadius = 8;
    private const double ContourStrokeThickness = 2;

    private DispatcherQueueTimer? _downloadProgressPulseTimer;
    private double _downloadProgressDisplayed;
    private bool _downloadHasRealProgress;
    private double _contourPathLength;
    private double _contourWidth;
    private double _contourHeight;
    private bool _contourUpdateQueued;
    private double _contourProgressPending;

    /// <summary>The reveal sequence owns the contour dissolve; suppress the snap-collapse.</summary>
    private bool _downloadContourFadingOut;

    private void OnViewModelDownloadProgressChanged(double progress)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnViewModelDownloadProgressChanged(progress));
            return;
        }

        if (progress > 0.02)
        {
            _downloadHasRealProgress = true;
        }

        QueueDownloadContourUpdate(Math.Max(_downloadProgressDisplayed, progress));
    }

    private void UpdateDownloadProgressChrome()
    {
        var showContour = ViewModel.IsFileDownloading || ViewModel.IsUpdateDownloading;
        if (!showContour)
        {
            StopDownloadProgressPulse();

            // During trim reveal the contour dissolves via DownloadContourFadeOutStoryboard;
            // don't snap it to Collapsed and don't reset state mid-fade.
            if (_downloadContourFadingOut)
            {
                return;
            }

            DownloadContourCanvas.Visibility = Visibility.Collapsed;
            ResetDownloadContourState();
            return;
        }

        DownloadContourCanvas.Visibility = Visibility.Visible;
        if (_downloadProgressPulseTimer is null)
        {
            StartDownloadProgressPulse();
        }
    }

    private void ResetDownloadContourState()
    {
        _downloadProgressDisplayed = 0;
        _downloadHasRealProgress = false;
        _contourPathLength = 0;
        _contourWidth = 0;
        _contourHeight = 0;
    }

    /// <summary>Dissolve the progress ring as part of the trim reveal (no abrupt snap-off).</summary>
    private void BeginDownloadContourFadeOut()
    {
        _downloadContourFadingOut = true;
        StopDownloadProgressPulse();
        DownloadContourFadeOutStoryboard.Stop();
        DownloadContourCanvas.Visibility = Visibility.Visible;
        DownloadContourCanvas.Opacity = 1;
        DownloadContourFadeOutStoryboard.Begin();
    }

    private void FinishDownloadContourFadeOut()
    {
        DownloadContourFadeOutStoryboard.Stop();
        DownloadContourCanvas.Visibility = Visibility.Collapsed;
        DownloadContourCanvas.Opacity = 1;
        _downloadContourFadingOut = false;
        ResetDownloadContourState();
    }

    private Task WaitForDownloadContourFadeOutAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(object? sender, object e)
        {
            DownloadContourFadeOutStoryboard.Completed -= OnCompleted;
            tcs.TrySetResult();
        }

        DownloadContourFadeOutStoryboard.Completed += OnCompleted;
        return Task.WhenAny(tcs.Task, Task.Delay(240));
    }

    private void StartDownloadProgressPulse()
    {
        StopDownloadProgressPulse();
        DownloadContourFadeOutStoryboard.Stop();
        _downloadContourFadingOut = false;
        _downloadProgressDisplayed = 0;
        _downloadHasRealProgress = false;
        DownloadContourCanvas.Opacity = 1;
        DownloadContourCanvas.Visibility = Visibility.Visible;
        QueueDownloadContourUpdate(0);

        _downloadProgressPulseTimer = DispatcherQueue.CreateTimer();
        _downloadProgressPulseTimer.Interval = TimeSpan.FromMilliseconds(80);
        _downloadProgressPulseTimer.Tick += (_, _) =>
        {
            var stillDownloading = ViewModel.IsFileDownloading || ViewModel.IsUpdateDownloading;
            if (_downloadHasRealProgress || !stillDownloading)
            {
                return;
            }

            QueueDownloadContourUpdate(Math.Min(0.08, _downloadProgressDisplayed + 0.02));
        };
        _downloadProgressPulseTimer.Start();
    }

    private void StopDownloadProgressPulse()
    {
        _downloadProgressPulseTimer?.Stop();
        _downloadProgressPulseTimer = null;
    }

    private void QueueDownloadContourUpdate(double progress)
    {
        _contourProgressPending = progress;
        if (_contourUpdateQueued)
        {
            return;
        }

        _contourUpdateQueued = true;
        var priority = _contourProgressPending >= 0.9
            ? DispatcherQueuePriority.Normal
            : DispatcherQueuePriority.Low;
        _ = DispatcherQueue.TryEnqueue(priority, () =>
        {
            _contourUpdateQueued = false;
            ApplyDownloadProgress(_contourProgressPending);
        });
    }

    private async Task AnimateContourToProgressAsync(double target, TimeSpan duration)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            await UiDispatcher.InvokeAsync(() => AnimateContourToProgressAsync(target, duration));
            return;
        }

        target = Math.Clamp(target, 0, 1);
        var start = _downloadProgressDisplayed;
        if (Math.Abs(start - target) < 0.004)
        {
            ApplyDownloadProgress(target);
            return;
        }

        var steps = Math.Max(1, (int)Math.Ceiling(duration.TotalMilliseconds / 16));
        for (var step = 1; step <= steps; step++)
        {
            var t = step / (double)steps;
            var eased = 1 - Math.Pow(1 - t, 3);
            ApplyDownloadProgress(start + (target - start) * eased);
            await Task.Delay(16);
        }
    }

    private void ApplyDownloadProgress(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        _downloadProgressDisplayed = Math.Max(_downloadProgressDisplayed, progress);

        var width = InputChromeHost.ActualWidth;
        var height = InputChromeHost.ActualHeight;
        if (width <= ContourStrokeThickness || height <= ContourStrokeThickness)
        {
            return;
        }

        DownloadContourCanvas.Visibility = Visibility.Visible;

        if (Math.Abs(width - _contourWidth) > 0.5 || Math.Abs(height - _contourHeight) > 0.5)
        {
            _contourWidth = width;
            _contourHeight = height;
            DownloadContourTrack.Data = ContourPathBuilder.CreateRoundedRectContour(
                width,
                height,
                ContourCornerRadius,
                ContourStrokeThickness);
            DownloadContourTrack.StrokeThickness = ContourStrokeThickness;
            var progressGeometry = ContourPathBuilder.CreateRoundedRectContour(
                width,
                height,
                ContourCornerRadius,
                ContourStrokeThickness);
            DownloadContourProgress.Data = progressGeometry;
            _contourPathLength = ContourPathBuilder.MeasurePathLength(progressGeometry);
        }

        if (_contourPathLength > 0)
        {
            ContourPathBuilder.ApplyProgressStroke(
                DownloadContourProgress,
                _contourPathLength,
                progress,
                ContourStrokeThickness);
        }
    }
}
