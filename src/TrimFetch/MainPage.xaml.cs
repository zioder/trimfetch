using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;
using TrimFetch.Helpers;
using System.Threading;
using TrimFetch.Models;
using TrimFetch.Services;
using TrimFetch.ViewModels;

namespace TrimFetch;

public sealed partial class MainPage : Page
{
    private const double ContentWidth = 680;
    private const double SectionGap = 16;
    private const double TrimVideoHeight = ContentWidth * 9 / 16;
    private const double TrimTimelineHeight = 64;
    private const double TrimPanelFallbackHeight = TrimVideoHeight + 12 + TrimTimelineHeight;
    private const double SectionCardBorderThickness = 2;
    private const double HistoryPanelPaddingVertical = 16;
    private const double MinHistoryListHeight = 80;

    private CancellationTokenSource? _pasteDebounce;
    private int _previousUrlLength;
    private bool _regionUpdateScheduled;
    private bool _isTrimPlaying;
    private bool _inputThumbnailEntranceActive;
    private SettingsWindow? _settingsWindow;
    private bool _pageInitialized;
    private bool _isUpdatingOverlayBounds;
    private double? _lockedWindowHeight;
    private List<WindowRegionHelper.RegionRect>? _cachedClipRegions;
    private ScrollViewer? _historyScrollViewer;
    private bool _isHistoryPointerOver;
    private bool _deferTrimSurfaceSchedule;
    private bool _isInputChromeHovered;
    private Task<int>? _downloadPrepTask;
    private string? _downloadPrepItemId;
    private readonly DispatcherTimer _trimPlaybackTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(50),
    };

    public MainPageViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        Loaded += MainPage_Loaded;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.DownloadCompletionUiAsync = RunDownloadCompletionUiAsync;
        ViewModel.StartDownloadTrimPrepAsync = EnsureDownloadTrimPrepAsync;
        ViewModel.CompleteDownloadWithTrimRevealAsync = CompleteDownloadWithTrimRevealAsync;
        ViewModel.ClipboardLaunchOrchestratorAsync = RunClipboardLaunchPipelineAsync;
        ViewModel.UpdateInstallRequested = RequestUpdateInstallAsync;
        ViewModel.DownloadProgressChanged += OnViewModelDownloadProgressChanged;
        ViewModel.GetTrimSelectionFromView = () => new TrimSelection(
            TrimTimeline.StartSeconds,
            TrimTimeline.EndSeconds);
        // Window resize must not be driven from SizeChanged (layout cycle with ResizeToContent).
        _trimPlaybackTimer.Tick += TrimPlaybackTimer_Tick;

        InputEntranceStoryboard.Completed += OnInputEntranceCompleted;
        HistoryEntranceStoryboard.Completed += OnHistoryEntranceCompleted;
        HotkeyRevealStoryboard.Completed += OnHotkeyRevealCompleted;

        AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(PageRoot_KeyDown), true);
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InverseBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static bool InverseBool(bool value) => !value;

    public static Visibility SetupItemInstallingVisibility(DependencySetupItemState state) =>
        state == DependencySetupItemState.Installing ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility SetupItemInstalledVisibility(DependencySetupItemState state) =>
        state == DependencySetupItemState.Installed ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility SetupItemMissingVisibility(DependencySetupItemState state) =>
        state == DependencySetupItemState.Missing ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility StringToVisibility(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static string TrimCopyGlyph(bool succeeded) => succeeded ? "\uE73E" : "\uE8C8";

    public string HistoryGifToolTip => ViewModel.HistoryCopyGifToolTip;

    public static Visibility HistoryGifIdleVisibility(bool isBusy, bool succeeded) =>
        isBusy || succeeded ? Visibility.Collapsed : Visibility.Visible;

    public static string TrimSaveGlyph(bool succeeded) => succeeded ? "\uE73E" : "\uE74E";

    public static Visibility TrimActionIdleVisibility(bool isBusy, bool succeeded) =>
        isBusy || succeeded ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Lays out clip regions while the window is still hidden and turns the frosted acrylic
    /// on before the first show, so the card shapes paint as glass — never black — on frame one.
    /// </summary>
    public void PrepareOverlayBeforeShow()
    {
        PrepareHotkeyShowVisualState();
        ApplyCardChrome(micaActive: true);

        if (!_pageInitialized)
        {
            if (App.Window is MainWindow earlyWindow)
            {
                earlyWindow.SetOverlayBackdrop(active: true);
            }

            return;
        }

        ContentStack.UpdateLayout();
        if (App.Window is MainWindow window)
        {
            InvalidateOverlaySize();
            UpdateOverlayBoundsCore(window);
            ContentStack.UpdateLayout();
            UpdateWindowClipRegion();

            // Enable the acrylic backdrop while still hidden so the clipped card shapes show
            // frosted glass on the very first painted frame instead of flashing black.
            window.SetOverlayBackdrop(active: true);
        }
    }

    /// <summary>Fades frosted cards in from a fully clear state after the HWND is shown.</summary>
    public void CompleteOverlayShow(bool focusInput = true)
    {
        ApplyCardChrome(micaActive: true);

        if (App.Window is MainWindow window)
        {
            window.SetOverlayBackdrop(active: true);
        }

        if (!_pageInitialized)
        {
            if (focusInput)
            {
                FocusUrlFieldWhenReady();
            }

            return;
        }

        UpdateWindowClipRegion();

        PlayHotkeyRevealAnimation(() =>
        {
            if (focusInput)
            {
                FocusUrlFieldWhenReady();
                ScheduleClipboardUrlOffer();
            }
        });
    }

    public void OnWindowActivated()
    {
        PresentOverlay(focusInput: false);
        ScheduleClipboardUrlOffer();
    }

    public void PresentDependencySetup()
    {
        ResetSectionVisualStates();

        if (App.Window is MainWindow window)
        {
            window.SetMicaActive(false);
            OverlayGlass.ApplyCard(SetupPanel, micaActive: false);
            ContentStack.UpdateLayout();
            window.ResizeToContent(460, 520, isSetup: true, anchorTopCenter: true);
            UpdateWindowClipRegion();
        }
    }

    private void CompleteDependencySetupAsync()
    {
        if (ViewModel.ShowDependencySetup)
        {
            return;
        }

        if (App.Window is MainWindow window)
        {
            window.ExitDependencySetupMode();
            window.SetMicaActive(true);
            ApplyCardChrome(micaActive: true);
        }

        _clipboardOfferPending = true;
        RunActivationAnimation();
        if (!IsEntranceAnimating)
        {
            OnOverlayEntranceFinished();
        }

        ScheduleRegionUpdate();
    }

    /// <summary>
    /// Makes section chrome visible, refreshes HWND clip regions, and focuses the URL field.
    /// Called from the global activation hotkey and after the window gains foreground.
    /// </summary>
    public void PresentOverlay(bool focusInput = true, bool playEntranceAnimation = false)
    {
        if (App.Window is MainWindow window)
        {
            window.SetMicaActive(!ViewModel.ShowDependencySetup);
        }

        if (ViewModel.ShowDependencySetup)
        {
            ResetSectionVisualStates();
        }
        else if (playEntranceAnimation && _hasCompletedInitialEntrance)
        {
            RunActivationAnimation(replay: true);
        }
        else if (!IsOverlayContentVisible() && _hasCompletedInitialEntrance && !_isInputEntranceActive)
        {
            RunActivationAnimation();
        }
        else if (!_hasCompletedInitialEntrance)
        {
            BeginInputEntrance();
        }
        else
        {
            EnsureOverlayContentVisible();
        }

        if (_pageInitialized)
        {
            SyncOverlayLayout();
        }
        else
        {
            _overlayResizePendingAfterEntrance = true;
        }

        if (!focusInput)
        {
            return;
        }

        FocusUrlFieldWhenReady();
        ScheduleClipboardUrlOffer();
    }

    public void OnMicaStateChanged(bool micaActive) => ApplyCardChrome(micaActive);

    public void OnWindowDeactivated()
    {
        if (_cachedClipRegions is { Count: > 0 })
        {
            ApplyCachedClipRegion();
        }

        ClearTransientChrome();
        StopTrimPlayback();

        if (!ViewModel.IsDownloadBusy && !ViewModel.IsTrimExporting)
        {
            // Keep the trim section and its loaded video intact so the next reveal restores exactly
            // what the user was working on (they may have clicked away to grab a link and still need
            // the clip). Only the transient URL draft is dropped. Playback is already paused above.
            ViewModel.PrepareForWindowHidden();
            RequestOverlayBoundsUpdate();
        }

        ScheduleRegionUpdate();
    }

    private void ApplyCardChrome(bool micaActive = false)
    {
        OverlayGlass.ApplySectionCard(InputChrome, micaActive);
        OverlayGlass.ApplySectionCard(TrimPanel, micaActive);
        OverlayGlass.ApplySectionCard(HistoryPanel, micaActive);
    }

    public void ApplyCachedClipRegion()
    {
        if (_cachedClipRegions is not { Count: > 0 } || App.Window is not MainWindow window)
        {
            return;
        }

        window.UpdateClipRegions(_cachedClipRegions);
    }

    private bool _overlayBoundsUpdateQueued;
    private long _overlayBoundsPassTick;
    private int _overlayBoundsPassCount;

    private void RequestOverlayBoundsUpdate(bool remeasure = false)
    {
        if (ShouldDeferOverlayBounds())
        {
            _overlayResizePendingAfterEntrance = true;
            return;
        }

        if (remeasure)
        {
            InvalidateOverlaySize();
        }

        if (_overlayBoundsUpdateQueued)
        {
            return;
        }

        _overlayBoundsUpdateQueued = true;
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _overlayBoundsUpdateQueued = false;
            UpdateOverlayBounds();
        });
    }

    private void ClearTransientChrome()
    {
        ResetSectionVisualStates();
        SetSettingsButtonVisible(false);
        SetTrimControlsVisible(false);
        SetUpdateButtonVisible(false);

        if (FocusManager.GetFocusedElement() is not null)
        {
            PageRoot.Focus(FocusState.Programmatic);
        }
    }

    private void InvalidateOverlaySize() => _lockedWindowHeight = null;

    /// <summary>Resize window and refresh clip regions in one synchronous UI pass (no deferred queue).</summary>
    public void SyncOverlayLayout()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(SyncOverlayLayout);
            return;
        }

        if (App.Window is not MainWindow window)
        {
            return;
        }

        InvalidateOverlaySize();
        ContentStack.UpdateLayout();
        UpdateOverlayBoundsCore(window);
        ContentStack.UpdateLayout();
        UpdateWindowClipRegion();
    }

    private static double HistoryPanelOuterHeight(double listMaxHeight) =>
        listMaxHeight + HistoryPanelPaddingVertical + SectionCardBorderThickness;

    private double GetTrimSectionHeight()
    {
        if (TrimPanel.Visibility == Visibility.Visible && TrimPanel.ActualHeight > 0)
        {
            return Math.Max(TrimPanel.ActualHeight, TrimPanelFallbackHeight);
        }

        return TrimPanelFallbackHeight + SectionCardBorderThickness;
    }

    /// <summary>Same as having clicked trim on an item already in history — list is visible before trim opens.</summary>
    private void EnsureDownloadInHistory(DownloadItem item)
    {
        if (ViewModel.History.Any(h => h.Id == item.Id))
        {
            return;
        }

        ViewModel.InsertHistoryItem(item);
        HistoryEntranceStoryboard.Stop();
        HistoryPanel.Opacity = 1;
        HistoryTransform.TranslateY = 0;
        InvalidateOverlaySize();
        SyncOverlayLayout();
    }

    private double GetHistorySectionHeight(double listMaxHeight)
    {
        if (HistoryPanel.Visibility == Visibility.Visible && HistoryPanel.ActualHeight > 0)
        {
            return HistoryPanel.ActualHeight;
        }

        return HistoryPanelOuterHeight(listMaxHeight);
    }

    public void FocusInputField() => FocusUrlFieldWhenReady();

    private void FocusUrlFieldWhenReady()
    {
        ViewModel.IsHistoryKeyboardMode = false;

        void FocusNow()
        {
            if (ViewModel.ShowDependencySetup)
            {
                return;
            }

            UrlTextBox.Focus(FocusState.Programmatic);
            MoveUrlCaretToEnd();
        }

        _ = DispatcherQueue.TryEnqueue(FocusNow);
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusNow);
    }

    private void MoveUrlCaretToEnd()
    {
        if (ViewModel.ShowDependencySetup)
        {
            return;
        }

        var length = UrlTextBox.Text?.Length ?? 0;
        UrlTextBox.SelectionStart = length;
        UrlTextBox.SelectionLength = 0;
    }

    private void ScheduleMoveUrlCaretToEnd()
    {
        _ = DispatcherQueue.TryEnqueue(MoveUrlCaretToEnd);
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, MoveUrlCaretToEnd);
    }

    public void ScheduleRegionUpdate()
    {
        if (_regionUpdateScheduled)
        {
            return;
        }

        _regionUpdateScheduled = true;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            _regionUpdateScheduled = false;
            UpdateWindowClipRegion();
        });
    }

    public void UpdateOverlayBounds()
    {
        if (_isUpdatingOverlayBounds || App.Window is not MainWindow window)
        {
            return;
        }

        _isUpdatingOverlayBounds = true;
        try
        {
            UpdateOverlayBoundsCore(window);
        }
        finally
        {
            _isUpdatingOverlayBounds = false;
        }
    }

    private void UpdateOverlayBoundsCore(MainWindow window)
    {
        if (ShouldDeferOverlayBounds())
        {
            _overlayResizePendingAfterEntrance = true;
            return;
        }

        var tick = Environment.TickCount64;
        if (tick == _overlayBoundsPassTick)
        {
            if (++_overlayBoundsPassCount > 2)
            {
                AppDiagnostic.Log("UpdateOverlayBounds: skipped (layout pass limit)");
                return;
            }
        }
        else
        {
            _overlayBoundsPassTick = tick;
            _overlayBoundsPassCount = 1;
        }

        if (ViewModel.ShowDependencySetup)
        {
            InvalidateOverlaySize();
            window.ResizeToContent(460, 520, isSetup: true);
            return;
        }

        var isCompact = !ViewModel.ShowVideo && !ViewModel.HasHistory;
        UpdateContentLayout(isCompact);

        var historyListMaxHeight = ViewModel.HistoryPanelHeight;
        var targetHeight = isCompact
            ? OverlayInputHeightDip
            : ResolveOverlayContentHeight(window, out historyListMaxHeight);

        if (!isCompact)
        {
            ApplyHistoryListMaxHeight(historyListMaxHeight);
        }

        if (_lockedWindowHeight.HasValue
            && Math.Abs(_lockedWindowHeight.Value - targetHeight) < 0.5)
        {
            ScheduleRegionUpdate();
            return;
        }

        var appliedHeight = window.ResizeToContent(ContentWidth, targetHeight, anchorTopCenter: !isCompact);
        _lockedWindowHeight = appliedHeight;

        if (!isCompact && Math.Abs(appliedHeight - targetHeight) > 0.5)
        {
            var refitHeight = ResolveOverlayContentHeight(window, out var refitHistoryMax);
            ApplyHistoryListMaxHeight(refitHistoryMax);
            _lockedWindowHeight = window.ResizeToContent(ContentWidth, refitHeight, anchorTopCenter: true);
        }

        ScheduleRegionUpdate();
    }

    private double ResolveOverlayContentHeight(MainWindow window, out double historyListMaxHeight)
    {
        historyListMaxHeight = ViewModel.HistoryPanelHeight;
        var desired = MeasureContentStackHeight();
        var maxHeight = window.GetMaxContentHeightDip();
        if (desired <= maxHeight + 0.5)
        {
            return desired;
        }

        if (!ViewModel.HasHistory)
        {
            return Math.Min(desired, maxHeight);
        }

        var baseHeight = MeasureContentStackHeight(includeHistory: false);
        var historyChrome = HistoryPanelPaddingVertical + SectionCardBorderThickness;
        historyListMaxHeight = Math.Clamp(
            maxHeight - baseHeight - SectionGap - historyChrome,
            MinHistoryListHeight,
            ViewModel.HistoryPanelHeight);
        return MeasureContentStackHeight(historyListMaxHeight);
    }

    private void ApplyHistoryListMaxHeight(double maxHeight)
    {
        if (ViewModel.HasHistory)
        {
            HistoryList.MaxHeight = maxHeight;
        }
    }

    private const double OverlayInputHeightDip = 64;

    private bool ShouldIncludeTrimSectionInHeight() =>
        (TrimPanel.Visibility == Visibility.Visible && !_excludeTrimFromOverlayHeight)
        || _includeTrimHeightInOverlayMeasure;

    /// <summary>Deterministic height from view-model flags — never reads ActualHeight (avoids layout cycles).</summary>
    private double MeasureContentStackHeight(double? historyListHeight = null, bool includeHistory = true)
    {
        var height = OverlayInputHeightDip;
        var sections = 1;

        if (ShouldIncludeTrimSectionInHeight())
        {
            height += GetTrimSectionHeight();
            sections++;
        }

        if (includeHistory && ViewModel.HasHistory)
        {
            var listHeight = historyListHeight ?? ViewModel.HistoryPanelHeight;
            height += GetHistorySectionHeight(listHeight);
            sections++;
        }

        if (sections > 1)
        {
            height += SectionGap * (sections - 1);
        }

        return height;
    }

    private void UpdateContentLayout(bool isCompact)
    {
        _ = isCompact;
        LayoutRoot.Padding = new Thickness(0);
        ContentStack.VerticalAlignment = VerticalAlignment.Top;
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        _ = InitializePageAsync();
    }

    private async Task InitializePageAsync()
    {
        try
        {
            ApplyFluentChrome();

            if (VideoPlayer.MediaPlayer is not null)
            {
                VideoPlayer.MediaPlayer.MediaOpened += VideoPlayer_MediaOpened;
                VideoPlayer.MediaPlayer.MediaFailed += VideoPlayer_MediaFailed;
                VideoPlayer.MediaPlayer.MediaEnded += VideoPlayer_MediaEnded;
            }

            await ViewModel.InitializeAsync();

            _hasCompletedInitialEntrance = true;
            _pageInitialized = true;

            await UiDispatcher.RunAsync(() =>
            {
                TrimPanel.UpdateLayout();
                TrimTimeline.RefreshLayout();
            });

            if (ViewModel.ShowDependencySetup)
            {
                if (App.Window is MainWindow setupWindow)
                {
                    ApplyCardChrome(micaActive: false);
                    setupWindow.EnterDependencySetupMode();
                }

                ScheduleRegionUpdate();

#if DEBUG
                if (SmokeTestLaunch.IsDownloadSmokeEnabled)
                {
                    _ = RunSmokeDownloadAsync();
                }
                else if (SmokeTestLaunch.IsEnabled)
                {
                    _ = RunSmokeTrimAsync();
                }
#endif
                return;
            }

            _previousUrlLength = ViewModel.SourceUrl.Length;

            if (ViewModel.ActiveTrimItem is not null)
            {
                ScheduleOpenTrimSurface();
            }

            if (App.Window is MainWindow window && window.ShouldRevealOnLaunch)
            {
                // Direct user launch: reveal the overlay with the same clean frosted fade-in
                // as the global hotkey, ready for a pasted URL (no black-box flash).
                window.RevealOverlayForLaunch();
            }
            else
            {
                // Login/startup launch: stay in the tray, but pre-warm the overlay visuals so
                // the first hotkey reveal is instant.
                if (App.Window is MainWindow trayWindow)
                {
                    trayWindow.HideOverlay();
                    ApplyCardChrome(trayWindow.IsMicaActive);
                }

                RunActivationAnimation();

                if (!IsEntranceAnimating)
                {
                    OnOverlayEntranceFinished();
                }
                else
                {
                    _clipboardOfferPending = true;
                }

                if (!_isInputEntranceActive)
                {
                    ScheduleRegionUpdate();
                }
            }

#if DEBUG
            if (SmokeTestLaunch.IsDownloadSmokeEnabled)
            {
                _ = RunSmokeDownloadAsync();
            }
            else if (SmokeTestLaunch.IsEnabled)
            {
                _ = RunSmokeTrimAsync();
            }
#endif
        }
        catch (Exception ex)
        {
            AppDiagnostic.LogException("MainPage init failed", ex);
            System.Diagnostics.Debug.WriteLine($"MainPage init failed: {ex}");
            ViewModel.StatusMessage = "Something went wrong starting the app.";
        }
    }

#if DEBUG
    private async Task RunSmokeDownloadAsync()
    {
        var url = SmokeTestLaunch.GetDownloadUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            AppDiagnostic.Log("smoke-download: FAIL no URL");
            return;
        }

        AppDiagnostic.Log($"smoke-download: start {url}");
        var deadline = DateTime.UtcNow.AddSeconds(SmokeTestLaunch.GetTimeoutSeconds());

        while (!ViewModel.DependenciesReady && DateTime.UtcNow < deadline)
        {
            await ViewModel.RefreshDependenciesAsync(force: true);
            await Task.Delay(250);
        }

        if (!ViewModel.DependenciesReady)
        {
            AppDiagnostic.Log("smoke-download: FAIL dependencies not ready");
            return;
        }

        EnsureOverlayContentVisible();
        UpdateDownloadProgressChrome();
        await Task.Delay(400);
        await ViewModel.StartSmokeDownloadAsync(url);
        AppDiagnostic.Log("smoke-download: finished");
    }

    private async Task RunSmokeTrimAsync()
    {
        AppDiagnostic.Log("smoke-trim: start");
        var timeoutSeconds = SmokeTestLaunch.GetTimeoutSeconds();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (!ExternalToolLocator.IsFfmpegAvailable && DateTime.UtcNow < deadline)
        {
            await ViewModel.RefreshDependenciesAsync(force: true);
            await Task.Delay(250);
        }

        if (!ExternalToolLocator.IsFfmpegAvailable)
        {
            AppDiagnostic.Log("smoke-trim: FAIL ffmpeg not available");
            ExitSmokeTrim(1);
            return;
        }

        DownloadItem item;
        var videoPath = SmokeTestLaunch.GetVideoPath();
        if (!string.IsNullOrWhiteSpace(videoPath))
        {
            item = ViewModel.RegisterSmokeTestVideo(videoPath);
            AppDiagnostic.Log($"smoke-trim: video={videoPath}");
        }
        else if (ViewModel.History.Count > 0)
        {
            item = ViewModel.History[0];
            AppDiagnostic.Log($"smoke-trim: history item={item.FileName}");
        }
        else
        {
            AppDiagnostic.Log("smoke-trim: FAIL no video");
            ExitSmokeTrim(1);
            return;
        }

        var expectedDimensions = await MediaProbeService.GetVideoDimensionsAsync(item.FilePath);
        var expectedAspect = expectedDimensions.IsValid
            ? expectedDimensions.AspectRatio
            : VideoDimensions.Default.AspectRatio;

        await RunOnUiThreadAsync(() => ViewModel.ActiveTrimItem = item);

        while (DateTime.UtcNow < deadline && !ViewModel.HasEnoughTimelineFrames())
        {
            await Task.Delay(200);
        }

        while (DateTime.UtcNow < deadline && !_isTrimPlaying)
        {
            await Task.Delay(200);
        }

        var (aspect, targetFrames, readyFrames, duration) = ViewModel.GetTimelineStripStatus();
        var fail = false;

        if (!_isTrimPlaying)
        {
            AppDiagnostic.Log("smoke-trim: FAIL autoplay did not start");
            fail = true;
        }

        if (!ViewModel.HasEnoughTimelineFrames())
        {
            AppDiagnostic.Log(
                $"smoke-trim: FAIL incomplete strip ready={readyFrames} target={targetFrames}");
            fail = true;
        }

        if (duration <= 0)
        {
            AppDiagnostic.Log("smoke-trim: FAIL duration not set");
            fail = true;
        }

        if (expectedDimensions.IsValid)
        {
            var aspectDelta = Math.Abs(aspect - expectedAspect);
            if (aspectDelta > 0.06)
            {
                AppDiagnostic.Log(
                    $"smoke-trim: FAIL aspect ui={aspect:0.###} expected={expectedAspect:0.###}");
                fail = true;
            }
        }

        if (ViewModel.TimelineFramePaths.Count > 0)
        {
            var frameDimensions = await MediaProbeService.GetVideoDimensionsAsync(
                ViewModel.TimelineFramePaths[0]);
            if (frameDimensions.IsValid && expectedDimensions.IsValid)
            {
                var frameAspect = frameDimensions.AspectRatio;
                var frameDelta = Math.Abs(frameAspect - expectedAspect);
                if (frameDelta > 0.12)
                {
                    AppDiagnostic.Log(
                        $"smoke-trim: FAIL frame aspect={frameAspect:0.###} expected={expectedAspect:0.###}");
                    fail = true;
                }
            }
        }

        AppDiagnostic.Log(
            $"smoke-trim: {(fail ? "FAIL" : "PASS")} frames={readyFrames}/{targetFrames} aspect={aspect:0.###} mediaReady={ViewModel.IsTrimMediaReady}");
        ExitSmokeTrim(fail ? 1 : 0);
    }

    private static void ExitSmokeTrim(int exitCode)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            App.DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
        });

        Environment.ExitCode = exitCode;
    }
#endif

    private void ApplyFluentChrome()
    {
        ApplyCardChrome(micaActive: true);
        OverlayGlass.ApplyCard(SetupPanel, micaActive: false);
    }

    private void UrlTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (TryHandleHistoryKeyboard(e))
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (ViewModel.IsDownloadBusy)
            {
                return;
            }

            _pasteDebounce?.Cancel();
            _ = ViewModel.CommitSourceUrlOnEnterAsync();
            return;
        }

        if (e.Key == VirtualKey.Tab)
        {
            e.Handled = true;
            FocusHistorySelection();
            return;
        }

        if (e.Key == VirtualKey.V
            && InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await Task.Delay(50);
                    ScheduleMoveUrlCaretToEnd();
                    await ViewModel.TryCommitSourceUrlAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Paste handler failed: {ex}");
                }
            });
        }
    }

    private CancellationTokenSource? _clipboardOfferCts;

    public void ScheduleClipboardUrlOffer()
    {
        if (ViewModel.ShowDependencySetup)
        {
            return;
        }

        if (!_pageInitialized || IsEntranceAnimating)
        {
            _clipboardOfferPending = true;
            return;
        }

        _clipboardOfferCts?.Cancel();
        _clipboardOfferCts = new CancellationTokenSource();
        var token = _clipboardOfferCts.Token;
        _ = OfferClipboardUrlOnActivateAsync(token);
    }

    private async Task OfferClipboardUrlOnActivateAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        _pasteDebounce?.Cancel();

        // WinRT clipboard is only reliable after the window is in the foreground.
        await Task.Delay(120, token);

        try
        {
            await WaitForStableOverlayAsync(token);
            await ViewModel.OfferClipboardUrlAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppDiagnostic.LogException("Clipboard URL offer", ex);
            System.Diagnostics.Debug.WriteLine($"Clipboard URL offer failed: {ex}");
        }
    }

    private async void UrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ViewModel.SuppressUrlAutoDownload)
        {
            ScheduleMoveUrlCaretToEnd();
            return;
        }

        var length = ViewModel.SourceUrl.Length;
        var likelyPaste = length - _previousUrlLength >= 12;
        _previousUrlLength = length;

        _pasteDebounce?.Cancel();
        _pasteDebounce = new CancellationTokenSource();
        var token = _pasteDebounce.Token;

        try
        {
            var delayMs = likelyPaste ? 80 : 550;
            await Task.Delay(delayMs, token);
            await ViewModel.TryCommitSourceUrlAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"URL auto-commit failed: {ex}");
        }
    }

    private void PageRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (ViewModel.ShowVideo
            && !ViewModel.IsAudioMode
            && ViewModel.CopyGifShortcut.Matches(e)
            && ViewModel.CopyTrimGifCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.CopyTrimGifCommand.Execute(null);
            return;
        }

        if (TryHandleHistoryKeyboard(e))
        {
            return;
        }

        if (ViewModel.ShowVideo && e.Key == VirtualKey.Space)
        {
            e.Handled = true;
            ToggleTrimPlayback();
            return;
        }

        if (ViewModel.ActivateAppShortcut.Matches(e))
        {
            e.Handled = true;
            PresentOverlay(focusInput: true, playEntranceAnimation: false);
            return;
        }

        if (e.Key == VirtualKey.Tab)
        {
            e.Handled = true;
            if (ViewModel.IsHistoryKeyboardMode)
            {
                ExitHistoryKeyboardNavigation();
            }
            else
            {
                FocusHistorySelection();
            }
        }
    }

    private bool TryHandleHistoryKeyboard(KeyRoutedEventArgs e)
    {
        if (ViewModel.ShowDependencySetup || ViewModel.History.Count == 0)
        {
            return false;
        }

        var index = ViewModel.IsHistoryKeyboardMode && ViewModel.SelectedHistoryItem is not null
            ? ViewModel.History.IndexOf(ViewModel.SelectedHistoryItem)
            : -1;

        if (e.Key == VirtualKey.Down)
        {
            if (!ViewModel.IsHistoryKeyboardMode)
            {
                EnterHistoryKeyboardNavigation(0);
                e.Handled = true;
                return true;
            }

            if (index < ViewModel.History.Count - 1)
            {
                EnterHistoryKeyboardNavigation(index + 1);
                e.Handled = true;
                return true;
            }

            return false;
        }

        if (e.Key == VirtualKey.Up)
        {
            if (!ViewModel.IsHistoryKeyboardMode)
            {
                return false;
            }

            if (index > 0)
            {
                EnterHistoryKeyboardNavigation(index - 1);
                e.Handled = true;
                return true;
            }

            ExitHistoryKeyboardNavigation();
            e.Handled = true;
            return true;
        }

        if (!ViewModel.IsHistoryKeyboardMode || ViewModel.SelectedHistoryItem is null)
        {
            return false;
        }

        if (ViewModel.OpenTrimShortcut.Matches(e))
        {
            e.Handled = true;
            ViewModel.EditTrimCommand.Execute(null);
            return true;
        }

        if (ViewModel.CopyShortcut.Matches(e))
        {
            e.Handled = true;
            ViewModel.CopyHistoryCommand.Execute(null);
            return true;
        }

        if (ViewModel.CopyHistoryGifShortcut.Matches(e)
            && ViewModel.CopyHistoryGifCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.CopyHistoryGifCommand.Execute(null);
            return true;
        }

        return false;
    }

    private void EnterHistoryKeyboardNavigation(int index)
    {
        if (ViewModel.History.Count == 0)
        {
            return;
        }

        ViewModel.IsHistoryKeyboardMode = true;
        ViewModel.SelectHistoryByIndex(index);
        ScrollSelectedHistoryIntoView();
        UrlTextBox.Focus(FocusState.Programmatic);
    }

    private void ExitHistoryKeyboardNavigation()
    {
        ViewModel.IsHistoryKeyboardMode = false;
        ViewModel.ClearHistoryNavigationHighlights();
        ViewModel.ClearHistoryPointerHighlights();
        ViewModel.SelectedHistoryItem = null;
        FocusUrlFieldWhenReady();
    }

    private void FocusHistorySelection() => EnterHistoryKeyboardNavigation(
        ViewModel.SelectedHistoryItem is null
            ? 0
            : ViewModel.History.IndexOf(ViewModel.SelectedHistoryItem));

    private void ScrollSelectedHistoryIntoView()
    {
        if (ViewModel.SelectedHistoryItem is null)
        {
            return;
        }

        HistoryList.ScrollIntoView(ViewModel.SelectedHistoryItem);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModel.ActiveTrimItem) && !_deferTrimSurfaceSchedule)
        {
            DispatcherQueue.TryEnqueue(ScheduleOpenTrimSurface);
        }

        if (e.PropertyName is nameof(ViewModel.TimelineFramePaths)
            or nameof(ViewModel.TimelineTargetFrameCount)
            or nameof(ViewModel.TimelineFrameAspectRatio))
        {
            if (ViewModel.IsTrimMediaReady)
            {
                DispatcherQueue.TryEnqueue(RefreshTrimTimelineLayout);
            }
        }

        if (e.PropertyName is nameof(ViewModel.HasInputPreviewThumbnail)
            or nameof(ViewModel.InputPreviewThumbnailPath))
        {
            DispatcherQueue.TryEnqueue(UpdateInputThumbnailChrome);
        }

        if (e.PropertyName is nameof(ViewModel.HasHistory) && ViewModel.HasHistory)
        {
            DispatcherQueue.TryEnqueue(EnsureHistoryScrollViewer);
        }

        if (e.PropertyName is nameof(ViewModel.HasHistory)
            or nameof(ViewModel.IsStatusVisible)
            or nameof(ViewModel.ShowDependencySetup)
            or nameof(ViewModel.DependenciesReady)
            or nameof(ViewModel.HistoryPanelHeight)
            or nameof(ViewModel.IsTrimExporting))
        {
            DispatcherQueue.TryEnqueue(HandleOverlaySectionsChanged);
        }

        if (e.PropertyName == nameof(ViewModel.DependenciesReady) && ViewModel.DependenciesReady)
        {
            DispatcherQueue.TryEnqueue(CompleteDependencySetupAsync);
        }

        if (e.PropertyName is nameof(ViewModel.IsTrimExporting))
        {
            DispatcherQueue.TryEnqueue(UpdateTrimControlsVisibility);
        }

        if (e.PropertyName is nameof(ViewModel.TrimStartSeconds)
            or nameof(ViewModel.TrimEndSeconds))
        {
            DispatcherQueue.TryEnqueue(ClampPlaybackDuringTrim);
        }

        if (e.PropertyName is nameof(ViewModel.IsFileDownloading))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateDownloadProgressChrome();
                if (!ViewModel.IsFileDownloading)
                {
                    FlushDeferredOverlayBounds();
                }
            });
        }

        if (e.PropertyName is nameof(ViewModel.IsUpdateDownloading)
            || e.PropertyName is nameof(ViewModel.UpdateState))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateDownloadProgressChrome();
                SetUpdateButtonVisible(_isInputChromeHovered);
                if (e.PropertyName is nameof(ViewModel.UpdateState))
                {
                    UpdateButtonColumn.Width = ViewModel.IsUpdateButtonVisible
                        ? new GridLength(28)
                        : new GridLength(0);
                }
            });
        }

        if (e.PropertyName is nameof(ViewModel.IsDownloading)
            && !ViewModel.IsDownloading)
        {
            DispatcherQueue.TryEnqueue(FlushDeferredOverlayBounds);
        }
    }

    private Task YieldUiDispatcherAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(() => completion.TrySetResult());
        return completion.Task;
    }

    private Task<int> EnsureDownloadTrimPrepAsync(DownloadItem item)
    {
        if (string.Equals(_downloadPrepItemId, item.Id, StringComparison.Ordinal)
            && _downloadPrepTask is not null)
        {
            return _downloadPrepTask;
        }

        _downloadPrepItemId = item.Id;
        _downloadPrepTask = RunDownloadTrimPrepAsync(item);
        return _downloadPrepTask;
    }

    private Task<int> RunDownloadTrimPrepAsync(DownloadItem item) =>
        UiDispatcher.InvokeAsync(async () =>
        {
            if (IsEntranceAnimating)
            {
                EnsureOverlayContentVisible();
                _hasCompletedInitialEntrance = true;
            }

            _revealTrimForDownloadCompletion = true;
            _excludeTrimFromOverlayHeight = true;
            _blockTrimVideoReveal = true;
            _deferTrimSurfaceSchedule = true;

            // Keep the ring moving through the hidden prep instead of freezing near full.
            StartTrimPrepCreep();

            ViewModel.ActiveTrimItem = item;
            var session = Interlocked.Increment(ref _trimSession);
            if (!await PrepareTrimSurfaceCoreAsync(session, item, CancellationToken.None))
            {
                AbortTrimOpen(session);
                return -1;
            }

            if (!IsDownloadTrimPrimedForReveal(item, session))
            {
                AbortTrimOpen(session);
                return -1;
            }

            if (!ViewModel.IsAudioMode)
            {
                // Only the placeholder strip (which reserves the filmstrip layout) and the video
                // prewarm gate the reveal. The real thumbnails are generated after the reveal by
                // CompleteTrimOpen's fire-and-forget EnsureTimelineStripAsync, so the video shows
                // as soon as it's ready instead of waiting on every ffmpeg still.
                await ViewModel.PrepareTimelineStripPlaceholdersAsync(item);
                AppDiagnostic.Log("TIMING prep: placeholders done");
                if (session != _trimSession)
                {
                    AbortTrimOpen(session);
                    return -1;
                }

                await PrewarmTrimVideoForRevealAsync(session);
                AppDiagnostic.Log("TIMING prep: prewarm done");
            }
            else
            {
                await ViewModel.EnsureWaveformPeaksAsync(item);
                if (session != _trimSession)
                {
                    AbortTrimOpen(session);
                    return -1;
                }
            }

            // The download is added to history only after the trim video is revealed
            // (see PlayDownloadCompletionRevealAsync), so the item slides into the list after
            // the video appears rather than being pre-listed during the hidden prep.
            SyncDownloadPrepOverlayBounds(session);

            TrimEntranceStoryboard.Stop();
            TrimTransform.TranslateY = 0;
            ViewModel.SetDownloadTrimReadyForReveal(true);
            return session;
        });

    private Task CompleteDownloadWithTrimRevealAsync(DownloadItem item) =>
        UiDispatcher.InvokeAsync(async () =>
        {
            var session = -1;
            try
            {
                AppDiagnostic.Log("TIMING completion: invoked (download done)");
                session = await EnsureDownloadTrimPrepAsync(item);
                AppDiagnostic.Log("TIMING completion: prep returned");
                if (session < 0
                    || !ViewModel.DownloadTrimReadyForReveal
                    || !IsDownloadTrimPrimedForReveal(item, session))
                {
                    throw new InvalidOperationException("Trim surface did not become ready.");
                }

                await PlayDownloadCompletionRevealAsync(session, item);
                await RunDownloadCompletionUiAsync(item);
                FlushDeferredOverlayBounds();
            }
            catch
            {
                if (session >= 0)
                {
                    AbortTrimOpen(session);
                }

                throw;
            }
            finally
            {
                StopTrimPrepCreep();
                _revealTrimForDownloadCompletion = false;
                _excludeTrimFromOverlayHeight = false;
                _includeTrimHeightInOverlayMeasure = false;
                _blockTrimVideoReveal = false;
                _deferTrimSurfaceSchedule = false;
                ViewModel.SetDownloadTrimReadyForReveal(false);
                _downloadPrepTask = null;
                _downloadPrepItemId = null;
            }
        });

    private async Task RunDownloadCompletionUiAsync(DownloadItem item)
    {
        DispatcherQueue.TryEnqueue(() => HistoryList.ScrollIntoView(item));

        await UiDispatcher.RunAsync(() =>
        {
            ViewModel.SourceUrl = string.Empty;
            _previousUrlLength = 0;
        });
    }

    private void ClampPlaybackDuringTrim()
    {
        if (!_isTrimPlaying || VideoPlayer.MediaPlayer is null)
        {
            return;
        }

        var (start, end) = CurrentBoundedTrimSelection();
        if (end <= start)
        {
            return;
        }

        var current = VideoPlayer.MediaPlayer.PlaybackSession.Position.TotalSeconds;
        if (!double.IsFinite(current))
        {
            return;
        }

        if (current < start || current >= end)
        {
            VideoPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(start);
            UpdateTrimPlaybackPosition();
        }
    }

    private void TrimTimeline_SelectionChanged(object sender, EventArgs e) =>
        RestartTrimFromSelection();

    private void TrimTimeline_SeekRequested(object sender, double seconds)
    {
        if (VideoPlayer.MediaPlayer is null || !ViewModel.ShowVideo || !ViewModel.IsTrimMediaReady)
        {
            return;
        }

        var (start, end) = CurrentBoundedTrimSelection();
        var position = Math.Clamp(seconds, start, end);

        EnsureTrimVideoVisible();
        VideoPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(position);
        ViewModel.TrimPlaybackPositionSeconds = position;
        UpdateTrimPlaybackPosition();

        if (_isTrimPlaying)
        {
            VideoPlayer.MediaPlayer.Play();
        }
    }

    private void RestartTrimFromSelection()
    {
        if (VideoPlayer.MediaPlayer is null || !ViewModel.ShowVideo || !ViewModel.IsTrimMediaReady)
        {
            return;
        }

        var (start, end) = CurrentBoundedTrimSelection();
        if (end <= start)
        {
            return;
        }

        EnsureTrimVideoVisible();
        VideoPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(start);
        UpdateTrimPlaybackPosition();

        if (_isTrimPlaying)
        {
            VideoPlayer.MediaPlayer.Play();
        }
    }


    private Task RunOnUiThreadAsync(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("Failed to enqueue UI callback."));
        }

        return completion.Task;
    }

    private void ReleaseVideoSource()
    {
        VideoPlayer.MediaPlayer?.Pause();
        VideoPlayer.Source = null;
        VideoPlayer.Visibility = Visibility.Collapsed;
    }

    private void VideoPlayer_MediaOpened(MediaPlayer sender, object args)
    {
        var duration = sender.PlaybackSession.NaturalDuration.TotalSeconds;
        if (double.IsFinite(duration) && duration > 0)
        {
            _trimMediaReadyTcs?.TrySetResult(true);
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (double.IsFinite(duration) && duration > 0)
            {
                ViewModel.MediaDurationSeconds = duration;
                ViewModel.TrimEndSeconds = duration;
            }

            if (ViewModel.ActiveTrimItem is { } trimItem
                && double.IsFinite(duration)
                && duration > 0)
            {
                ViewModel.RememberTimelineDuration(trimItem, duration);
            }

        });
    }

    private void RefreshTrimTimelineLayout()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshTrimTimelineLayout);
            return;
        }

        if (!ViewModel.ShowVideo)
        {
            return;
        }

        TrimTimeline.RefreshLayout();
    }

    private void VideoPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() => OnTrimMediaFailed(_trimSession));
    }

    private void VideoPlayer_MediaEnded(MediaPlayer sender, object args) =>
            DispatcherQueue.TryEnqueue(StopTrimPlayback);

    private void PlayTrim_Click(object sender, RoutedEventArgs e) => ToggleTrimPlayback();

    private void ToggleTrimPlayback()
    {
        if (VideoPlayer.MediaPlayer is null || !ViewModel.ShowVideo)
        {
            return;
        }

        if (_isTrimPlaying)
        {
            StopTrimPlayback();
            return;
        }

        StartTrimPlaybackFromSelection();
    }

    private void StartTrimPlaybackFromSelection()
    {
        if (VideoPlayer.MediaPlayer is null || !ViewModel.ShowVideo)
        {
            return;
        }

        if (ViewModel.IsAudioMode)
        {
            ApplyTrimPreviewSurface(audio: true);
        }
        else
        {
            RevealVideo();
        }

        var (start, end) = GetTrimPlaybackRange();
        if (end <= start)
        {
            return;
        }

        var current = VideoPlayer.MediaPlayer.PlaybackSession.Position.TotalSeconds;
        if (double.IsFinite(current) && current >= start && current < end - 0.001)
        {
            start = current;
        }

        VideoPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(start);
        VideoPlayer.MediaPlayer.Play();
        _trimPlaybackTimer.Start();
        _isTrimPlaying = true;
        PlayPauseIcon.Glyph = "\uE769";
        UpdateTrimPlaybackPosition();
        UpdateTrimControlsVisibility();
    }

    private void StopTrimPlayback() => StopTrimPlayback(seekToSelectionEnd: false);

    private void StopTrimPlayback(bool seekToSelectionEnd)
    {
        if (VideoPlayer.MediaPlayer is null)
        {
            return;
        }

        VideoPlayer.MediaPlayer.Pause();
        _trimPlaybackTimer.Stop();
        _isTrimPlaying = false;
        PlayPauseIcon.Glyph = "\uE768";

        if (seekToSelectionEnd)
        {
            var (_, end) = CurrentBoundedTrimSelection();
            VideoPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(end);
        }

        UpdateTrimPlaybackPosition();
        UpdateTrimControlsVisibility();
    }

    private void UpdateTrimPlaybackPosition()
    {
        if (VideoPlayer.MediaPlayer?.PlaybackSession is null)
        {
            ViewModel.TrimPlaybackPositionSeconds = 0;
            return;
        }

        var current = VideoPlayer.MediaPlayer.PlaybackSession.Position.TotalSeconds;
        ViewModel.TrimPlaybackPositionSeconds = double.IsFinite(current) ? current : 0;
    }

    private void TrimPlaybackTimer_Tick(object? sender, object e)
    {
        UpdateTrimPlaybackPosition();
        StopIfPlaybackReachedSelectionEnd();
    }

    private void StopIfPlaybackReachedSelectionEnd()
    {
        if (!_isTrimPlaying || VideoPlayer.MediaPlayer is null)
        {
            return;
        }

        var (start, end) = CurrentBoundedTrimSelection();
        var current = VideoPlayer.MediaPlayer.PlaybackSession.Position.TotalSeconds;
        if (!double.IsFinite(current))
        {
            return;
        }

        if (current >= end - 0.001)
        {
            StopTrimPlayback(seekToSelectionEnd: true);
        }
        else if (current < start)
        {
            VideoPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(start);
        }
    }

    private (double Start, double End) CurrentBoundedTrimSelection() => GetTrimPlaybackRange();

    private void TrimPanel_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetTrimControlsVisible(true);

    private void TrimPanel_PointerExited(object sender, PointerRoutedEventArgs e) =>
        SetTrimControlsVisible(false);

    private void SetTrimControlsVisible(bool visible)
    {
        if (ViewModel.IsTrimExporting)
        {
            TrimControls.Opacity = 1;
            TrimControls.IsHitTestVisible = true;
            return;
        }

        TrimControls.Opacity = visible ? 1 : 0;
        TrimControls.IsHitTestVisible = visible;
    }

    private void UpdateTrimControlsVisibility() => SetTrimControlsVisible(TrimControls.Opacity > 0);

    private void UpdateWindowClipRegion()
    {
        if (App.Window is not MainWindow window)
        {
            return;
        }

        if (ViewModel.ShowDependencySetup)
        {
            _cachedClipRegions = CollectRegions(SetupPanel, 8).ToList();
            window.UpdateClipRegions(_cachedClipRegions);
            return;
        }

        var regions = new List<WindowRegionHelper.RegionRect>();
        regions.AddRange(CollectRegions(InputChromeHost, GetInputCornerRadius()));

        if (ShouldIncludeTrimSectionInHeight())
        {
            regions.AddRange(CollectRegions(TrimPanel, 8));
        }

        if (ViewModel.HasHistory)
        {
            HistoryPanel.UpdateLayout();
            regions.AddRange(CollectRegions(HistoryPanel, 8));
        }

        if (regions.Count == 0)
        {
            return;
        }

        _cachedClipRegions = regions;
        window.UpdateClipRegions(regions);
    }

    private int GetInputCornerRadius()
    {
        var radius = InputChrome.CornerRadius.TopLeft;
        return radius > 0 ? (int)Math.Round(radius) : 4;
    }

    private IReadOnlyList<WindowRegionHelper.RegionRect> CollectRegions(FrameworkElement element, int cornerRadius)
    {
        if (element.Visibility != Visibility.Visible
            || element.ActualWidth <= 0
            || element.ActualHeight <= 0)
        {
            return [];
        }

        try
        {
            var transform = element.TransformToVisual(PageRoot);
            var point = transform.TransformPoint(new Point(0, 0));
            var scale = element.XamlRoot?.RasterizationScale ?? 1.0;

            var left = (int)Math.Floor(point.X * scale);
            var top = (int)Math.Floor(point.Y * scale);
            var right = (int)Math.Ceiling((point.X + element.ActualWidth) * scale);
            var bottom = (int)Math.Ceiling((point.Y + element.ActualHeight) * scale);
            var width = right - left;
            var height = bottom - top;

            return
            [
                new WindowRegionHelper.RegionRect(
                    left,
                    top,
                    width,
                    height,
                    (int)Math.Round(cornerRadius * scale)),
            ];
        }
        catch
        {
            return [];
        }
    }

    private void InputThumbnailEntranceStoryboard_Completed(object? sender, object e) =>
        _inputThumbnailEntranceActive = false;

    private void UpdateInputThumbnailChrome()
    {
        if (ViewModel.HasInputPreviewThumbnail)
        {
            InputThumbnailColumn.Width = new GridLength(56);
            InputThumbnailHost.Visibility = Visibility.Visible;

            if (!_inputThumbnailEntranceActive)
            {
                _inputThumbnailEntranceActive = true;
                InputThumbnailTranslate.X = -56;
                InputThumbnailHost.Opacity = 0;
                InputThumbnailEntranceStoryboard.Completed -= InputThumbnailEntranceStoryboard_Completed;
                InputThumbnailEntranceStoryboard.Completed += InputThumbnailEntranceStoryboard_Completed;
                InputThumbnailEntranceStoryboard.Begin();
            }

            return;
        }

        _inputThumbnailEntranceActive = false;
        InputThumbnailEntranceStoryboard.Completed -= InputThumbnailEntranceStoryboard_Completed;
        InputThumbnailEntranceStoryboard.Stop();
        InputThumbnailColumn.Width = new GridLength(0);
        InputThumbnailHost.Visibility = Visibility.Collapsed;
        InputThumbnailImage.Source = null;
    }

    private void InputChrome_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isInputChromeHovered = true;
        SetSettingsButtonVisible(true);
        SetUpdateButtonVisible(true);
    }

    private void InputChrome_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isInputChromeHovered = false;
        SetSettingsButtonVisible(false);
        SetUpdateButtonVisible(false);
    }

    private void SetSettingsButtonVisible(bool visible)
    {
        SettingsButton.Opacity = visible ? 1 : 0;
        SettingsButton.IsHitTestVisible = visible;
    }

    private void SetUpdateButtonVisible(bool visible)
    {
        if (UpdateButton.Visibility != Visibility.Visible)
        {
            UpdateButton.Opacity = 0;
            UpdateButton.IsHitTestVisible = false;
            return;
        }

        UpdateButton.Opacity = visible ? 1 : 0;
        UpdateButton.IsHitTestVisible = visible;
    }

    /// <summary>
    /// Launches the downloaded installer and closes the app. Returns false (leaving the VM in
    /// the Downloaded state, so the update button stays live for a one-click retry) when the
    /// launch fails.
    /// </summary>
    private Task<bool> RequestUpdateInstallAsync(DownloadedUpdate update)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(update.FilePath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppDiagnostic.LogException("Update launch", ex);
            ViewModel.StatusMessage = $"Couldn't launch installer: {ex.Message}";
            return Task.FromResult(false);
        }

        if (App.Window is MainWindow mainWindow)
        {
            mainWindow.ExitApplication();
        }
        else
        {
            Application.Current.Exit();
        }

        return Task.FromResult(true);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    public void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            WireSettingsWindowEvents();
        }

        _settingsWindow.Activate();
        NativeWindowForeground.BringToForeground(
            WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow));
    }

    private void WireSettingsWindowEvents()
    {
        if (_settingsWindow is null)
        {
            return;
        }

        _settingsWindow.HotKeysChanged -= SettingsWindow_HotKeysChanged;
        _settingsWindow.HotKeysChanged += SettingsWindow_HotKeysChanged;
        _settingsWindow.DependenciesChanged -= SettingsWindow_DependenciesChanged;
        _settingsWindow.DependenciesChanged += SettingsWindow_DependenciesChanged;
        _settingsWindow.PreferencesChanged -= SettingsWindow_PreferencesChanged;
        _settingsWindow.PreferencesChanged += SettingsWindow_PreferencesChanged;
        _settingsWindow.Closed -= SettingsWindow_Closed;
        _settingsWindow.Closed += SettingsWindow_Closed;
    }

    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        // The window is destroyed on close; drop the cached reference so the next open
        // creates a fresh window instead of trying to activate a closed one.
        if (_settingsWindow is null)
        {
            return;
        }

        _settingsWindow.HotKeysChanged -= SettingsWindow_HotKeysChanged;
        _settingsWindow.DependenciesChanged -= SettingsWindow_DependenciesChanged;
        _settingsWindow.PreferencesChanged -= SettingsWindow_PreferencesChanged;
        _settingsWindow.Closed -= SettingsWindow_Closed;
        _settingsWindow = null;
    }

    private async void SettingsWindow_DependenciesChanged(object? sender, EventArgs e) =>
        await ViewModel.RefreshDependenciesAsync(force: true);

    private async void SettingsWindow_PreferencesChanged(object? sender, EventArgs e) =>
        await ViewModel.ReloadPreferencesAsync();

    private async void SettingsWindow_HotKeysChanged(object? sender, EventArgs e)
    {
        await ViewModel.ReloadHotKeysAsync();
        if (App.Window is MainWindow window)
        {
            await window.ReloadActivationHotKeyAsync();
        }
    }

    private void VideoModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.ShowTrimVideoModeToggle)
        {
            return;
        }

        SetTrimAudioMode(audio: false);
    }

    private void AudioModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.ShowTrimAudioModeToggle)
        {
            return;
        }

        SetTrimAudioMode(audio: true);
    }

    private void TrimAudioFormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ViewModel.IsAudioMode || TrimAudioFormatCombo.SelectedIndex < 0)
        {
            return;
        }

        ViewModel.TrimAudioFormat = TrimAudioFormatCombo.SelectedIndex == 1
            ? TrimAudioFormat.Wav
            : TrimAudioFormat.Mp3;
    }

    private void SetTrimAudioMode(bool audio)
    {
        if (audio && !ViewModel.ShowTrimAudioModeToggle)
        {
            audio = false;
        }

        if (!audio && !ViewModel.ShowTrimVideoModeToggle)
        {
            audio = true;
        }

        VideoModeButton.IsChecked = !audio;
        AudioModeButton.IsChecked = audio;

        if (ViewModel.IsAudioMode != audio)
        {
            ViewModel.IsAudioMode = audio;
        }

        ApplyTrimPreviewSurface(audio);

        if (audio && ViewModel.ActiveTrimItem is { } item)
        {
            _ = ViewModel.EnsureWaveformPeaksAsync(item);
        }

        TrimTimeline.RefreshLayout();
    }

    private void ApplyTrimPreviewSurface(bool audio)
    {
        AudioModePlaceholder.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;

        if (audio)
        {
            VideoPlayer.Visibility = Visibility.Collapsed;
            TrimPosterImage.Visibility = Visibility.Collapsed;
            return;
        }

        if (ViewModel.IsTrimMediaReady && !_blockTrimVideoReveal)
        {
            RevealVideo();
        }
    }

    private void CloseTrim_Click(object sender, RoutedEventArgs e) =>
        ViewModel.CloseTrimCommand.Execute(null);

    private void HistoryList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            if (args.ItemContainer is ListViewItem recycled)
            {
                recycled.PointerEntered -= HistoryListItem_PointerEntered;
                recycled.PointerExited -= HistoryListItem_PointerExited;
            }

            return;
        }

        if (args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        container.PointerEntered -= HistoryListItem_PointerEntered;
        container.PointerExited -= HistoryListItem_PointerExited;
        container.PointerEntered += HistoryListItem_PointerEntered;
        container.PointerExited += HistoryListItem_PointerExited;
    }

    private void HistoryListItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ListViewItem { Content: DownloadItem item })
        {
            ViewModel.SetHistoryPointerHighlight(item);
        }
    }

    private void HistoryListItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ListViewItem { Content: DownloadItem item })
        {
            item.IsPointerHighlighted = false;
        }
    }

    private void HistoryList_PointerExited(object sender, PointerRoutedEventArgs e) =>
        ViewModel.ClearHistoryPointerHighlights();

    private void HistoryList_Loaded(object sender, RoutedEventArgs e) =>
        EnsureHistoryScrollViewer();

    private void HistoryPanel_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHistoryPointerOver = true;
        UpdateHistoryScrollBarVisibility();
    }

    private void HistoryPanel_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHistoryPointerOver = false;
        UpdateHistoryScrollBarVisibility();
    }

    private void EnsureHistoryScrollViewer()
    {
        _historyScrollViewer ??= FindDescendant<ScrollViewer>(HistoryList);
        UpdateHistoryScrollBarVisibility();
    }

    private void UpdateHistoryScrollBarVisibility()
    {
        if (_historyScrollViewer is null)
        {
            _historyScrollViewer = FindDescendant<ScrollViewer>(HistoryList);
        }

        if (_historyScrollViewer is null)
        {
            return;
        }

        _historyScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _historyScrollViewer.VerticalScrollBarVisibility = _isHistoryPointerOver
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Hidden;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DownloadItem item)
        {
            return;
        }

        SelectHistoryItem(item);
    }

    private void SelectHistoryItem(DownloadItem item)
    {
        ViewModel.IsHistoryKeyboardMode = true;
        ViewModel.SelectHistoryByIndex(ViewModel.History.IndexOf(item));
        ScrollSelectedHistoryIntoView();
        UrlTextBox.Focus(FocusState.Programmatic);
    }

    private void HistoryTrim_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DownloadItem item })
        {
            SelectHistoryItem(item);
            ViewModel.EditTrimCommand.Execute(null);
        }
    }

    private void HistoryCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DownloadItem item })
        {
            return;
        }

        SelectHistoryItem(item);
        ViewModel.CopyHistoryCommand.Execute(null);
    }

    private void HistoryGif_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DownloadItem item })
        {
            return;
        }

        SelectHistoryItem(item);
        ViewModel.CopyHistoryGifCommand.Execute(null);
    }

    private async void HistoryMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not DownloadItem item)
        {
            return;
        }

        ViewModel.SelectedHistoryItem = item;

        var flyout = new MenuFlyout();

        var reveal = new MenuFlyoutItem
        {
            Text = "Show in folder",
            Icon = new FontIcon { Glyph = "\uE838" },
        };
        reveal.Click += (_, _) => ViewModel.RevealHistoryCommand.Execute(null);
        flyout.Items.Add(reveal);

        var openLink = new MenuFlyoutItem
        {
            Text = "Open link",
            Icon = new FontIcon { Glyph = "\uE71B" },
        };
        openLink.Click += (_, _) => ViewModel.OpenSourceCommand.Execute(null);
        flyout.Items.Add(openLink);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var delete = new MenuFlyoutItem
        {
            Text = "Delete",
            Icon = new FontIcon { Glyph = "\uE74D" },
        };
        delete.Click += async (_, _) => await ViewModel.DeleteHistoryCommand.ExecuteAsync(null);
        flyout.Items.Add(delete);

        flyout.ShowAt(button);
    }
}
