using TrimFetch.Helpers;

namespace TrimFetch;

public sealed partial class MainPage
{
    private bool _clipboardOfferPending;
    private bool _overlayResizePendingAfterEntrance;

    private bool IsEntranceAnimating => _isInputEntranceActive || _isHistoryEntranceActive;

    private bool ShouldDeferOverlayBounds() =>
        !_pageInitialized
        || IsEntranceAnimating
        || (ViewModel.IsFileDownloading && !_revealTrimForDownloadCompletion)
        || (ViewModel.IsDownloading
            && !_revealTrimForDownloadCompletion
            && !ViewModel.DownloadTrimReadyForReveal)
        || _clipboardLaunchUrlPhaseActive;

    private async Task WaitForStableOverlayAsync(CancellationToken cancellationToken = default)
    {
        while ((!_pageInitialized || IsEntranceAnimating) && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(50, cancellationToken);
        }
    }

    private void OnOverlayEntranceFinished()
    {
        FlushDeferredOverlayBounds();
        UpdateWindowClipRegion();

        if (_clipboardOfferPending)
        {
            _clipboardOfferPending = false;
            ScheduleClipboardUrlOffer();
        }
    }

    private void FlushDeferredOverlayBounds()
    {
        if (ShouldDeferOverlayBounds())
        {
            return;
        }

        RequestOverlayBoundsUpdate(remeasure: _overlayResizePendingAfterEntrance);
        _overlayResizePendingAfterEntrance = false;
    }

    internal void OnLayoutException(Exception ex)
    {
        if (ex is not Microsoft.UI.Xaml.LayoutCycleException)
        {
            return;
        }

        _overlayBoundsUpdateQueued = false;
        _overlayResizePendingAfterEntrance = true;
        AppDiagnostic.Log("LayoutCycle: deferred bounds to next milestone");
    }
}
