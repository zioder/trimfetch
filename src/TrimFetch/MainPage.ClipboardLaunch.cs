using TrimFetch.Helpers;

namespace TrimFetch;

public sealed partial class MainPage
{
    private readonly SemaphoreSlim _clipboardLaunchGate = new(1, 1);
    private bool _clipboardLaunchUrlPhaseActive;
    private const int UrlVisibleBeatMs = 220;

    private async Task RunClipboardLaunchPipelineAsync(string url)
    {
        await _clipboardLaunchGate.WaitAsync();
        try
        {
            var existing = ViewModel.FindDownloadedHistoryItem(url);
            if (existing is not null)
            {
                await WaitForStableOverlayAsync();
                await UiDispatcher.RunAsync(() => ViewModel.FocusExistingHistoryItem(existing, url));
                return;
            }

            // Show URL immediately so it appears with the entrance animation.
            AppDiagnostic.Log($"Clipboard launch: show url {url}");
            ViewModel.SuppressUrlAutoDownload = true;
            _clipboardLaunchUrlPhaseActive = true;
            try
            {
                ViewModel.SourceUrl = url;
                ViewModel.StatusMessage = string.Empty;
                _previousUrlLength = url.Length;
                ScheduleMoveUrlCaretToEnd();

                // Wait for overlay entrance to finish, then hold briefly so user sees the URL.
                await WaitForStableOverlayAsync();
                await Task.Delay(UrlVisibleBeatMs);
            }
            finally
            {
                _clipboardLaunchUrlPhaseActive = false;
                ViewModel.SuppressUrlAutoDownload = false;
                FlushDeferredOverlayBounds();
            }

            AppDiagnostic.Log("Clipboard launch: start download");
            await ViewModel.StartDownloadFromUrlAsync(url);
        }
        catch (Exception ex)
        {
            AppDiagnostic.LogException("Clipboard launch", ex);
        }
        finally
        {
            _clipboardLaunchGate.Release();
        }
    }
}
