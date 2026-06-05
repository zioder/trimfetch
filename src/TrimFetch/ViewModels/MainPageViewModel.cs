using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrimFetch.Helpers;
using TrimFetch.Models;
using TrimFetch.Services;

namespace TrimFetch.ViewModels;

public enum AppUpdateState
{
    None,
    Checking,
    Available,
    Downloading,
    Downloaded,
    Failed,
}

public partial class MainPageViewModel : ObservableObject
{
    private readonly AppStorageService _storage = new();
    private readonly PreferencesService _preferences = new();
    private readonly MediaDownloaderService _downloader = new();
    private readonly TrimExportService _trimExporter = new();
    private readonly ClipboardService _clipboard = new();
    private readonly FilePickerService _filePicker = new();
    private readonly ThumbnailStripService _thumbnailStrip = new();
    private readonly WaveformStripService _waveformStrip = new();
    private readonly ThumbnailGeneratorService _thumbnailGenerator = new();
    private readonly VideoThumbnailFetchService _thumbnailFetch = new();
    private readonly DependencySetupService _dependencySetup = new();
    private readonly UpdateCheckerService _updateChecker = new();
    private CancellationTokenSource? _downloadCts;
    private int _downloadSession;
    private string? _inFlightDownloadUrl;
    private int _downloadStagesCompleted;
    private double _lastDownloadFraction;
    private double _lastPostProcessFraction;
    private double _downloadSegmentHighWater;
    private (string Path, TrimMediaProfile Profile)? _prewarmedTrimProfile;
    private const int DependencyCheckTtlMs = 30_000;
    private static readonly SemaphoreSlim ThumbnailGenerationGate = new(2, 2);
    private bool _depsStatusValid;
    private DateTimeOffset _depsStatusCheckedUtc;
    private bool _downloadProgressDispatchQueued;
    private double _pendingDownloadProgress;
    private const double MaxHistoryGifSeconds = 40;
    private readonly SemaphoreSlim _timelineLoadGate = new(1, 1);
    private readonly Dictionary<string, double> _probedDurations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VideoDimensions> _probedDimensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _timelineStripTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _timelineStripTokens = new(StringComparer.Ordinal);
    private string? _timelineStripItemId;
    private int _lastPublishedTimelineFrameCount;
    private DateTimeOffset _lastTimelinePublishUtc = DateTimeOffset.MinValue;
    private const int TimelinePublishMinIntervalMs = 150;
    private string? _lastClipboardOfferFingerprint;
    private bool _suppressHistoryNotifications;
    private CancellationTokenSource? _historySaveDebounceCts;
    private UpdateCheckResult? _pendingUpdateResult;
    private DownloadedUpdate? _downloadedUpdate;
    private int _updateCheckSession;

    public bool SuppressUrlAutoDownload { get; set; }

    private DownloadQualityPreset _downloadQuality = DownloadQualityPreset.Best;

    public bool AutoDownloadClipboardUrls { get; private set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyPropertyChangedFor(nameof(IsUpdateOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(EffectiveUrlPlaceholderText))]
    public partial string SourceUrl { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadFolderName))]
    public partial string DownloadFolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartUpdateDownloadCommand))]
    [NotifyPropertyChangedFor(nameof(IsDownloadBusy))]
    [NotifyPropertyChangedFor(nameof(IsInputLocked))]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial bool IsFileDownloading { get; set; }

    [ObservableProperty]
    public partial double DownloadProgress { get; set; }

    /// <summary>History insert and URL clear after trim is revealed.</summary>
    public Func<DownloadItem, Task>? DownloadCompletionUiAsync { get; set; }

    /// <summary>Hidden prep while the bar stays below 100% (media + timeline placeholders).</summary>
    public Func<DownloadItem, Task<int>>? StartDownloadTrimPrepAsync { get; set; }

    /// <summary>Bar to 100%, then contour fade and trim entrance (everything must already be ready).</summary>
    public Func<DownloadItem, Task>? CompleteDownloadWithTrimRevealAsync { get; set; }

    /// <summary>Ordered clipboard flow: show URL, download, trim, then history.</summary>
    public Func<string, Task>? ClipboardLaunchOrchestratorAsync { get; set; }

    /// <summary>
    /// Launches the downloaded installer and exits the app. Returns <c>true</c> only when the
    /// installer launched and the app is shutting down; <c>false</c> when the launch failed
    /// (the VM stays in <see cref="AppUpdateState.Downloaded"/> so the install can be retried).
    /// </summary>
    public Func<DownloadedUpdate, Task<bool>>? UpdateInstallRequested { get; set; }

    /// <summary>Raised on the UI thread when yt-dlp reports download percent.</summary>
    public event Action<double>? DownloadProgressChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputPreviewThumbnail))]
    public partial string? InputPreviewThumbnailPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDependencySetup))]
    [NotifyCanExecuteChangedFor(nameof(RecheckDependenciesCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallDependenciesCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial bool DependenciesReady { get; set; } = true;

    [ObservableProperty]
    public partial bool IsFfmpegAvailable { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetupVerifyButtonText))]
    [NotifyCanExecuteChangedFor(nameof(InstallDependenciesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RecheckDependenciesCommand))]
    public partial bool IsInstallingDependencies { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetupInstallButtonText))]
    [NotifyPropertyChangedFor(nameof(SetupMissingToolsSummary))]
    [NotifyPropertyChangedFor(nameof(YtDlpSetupStatusText))]
    public partial bool HasYtDlp { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetupInstallButtonText))]
    [NotifyPropertyChangedFor(nameof(SetupMissingToolsSummary))]
    [NotifyPropertyChangedFor(nameof(FfmpegSetupStatusText))]
    public partial bool HasFfmpeg { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YtDlpSetupStatusText))]
    [NotifyPropertyChangedFor(nameof(FfmpegSetupStatusText))]
    [NotifyPropertyChangedFor(nameof(WingetSetupStatusText))]
    [NotifyPropertyChangedFor(nameof(SetupMissingToolsSummary))]
    public partial DependencySetupItemState YtDlpSetupState { get; set; } = DependencySetupItemState.Missing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YtDlpSetupStatusText))]
    [NotifyPropertyChangedFor(nameof(FfmpegSetupStatusText))]
    [NotifyPropertyChangedFor(nameof(WingetSetupStatusText))]
    [NotifyPropertyChangedFor(nameof(SetupMissingToolsSummary))]
    public partial DependencySetupItemState FfmpegSetupState { get; set; } = DependencySetupItemState.Missing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WingetSetupStatusText))]
    [NotifyPropertyChangedFor(nameof(SetupMissingToolsSummary))]
    public partial DependencySetupItemState WingetSetupState { get; set; } = DependencySetupItemState.Missing;

    [ObservableProperty]
    public partial string SetupProgressMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double MediaDurationSeconds { get; set; } = 15;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusVisible))]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(IsUpdateDownloading))]
    [NotifyPropertyChangedFor(nameof(IsUpdateDownloaded))]
    [NotifyPropertyChangedFor(nameof(IsUpdateBusy))]
    [NotifyPropertyChangedFor(nameof(IsInputLocked))]
    [NotifyPropertyChangedFor(nameof(IsUpdateOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(UpdateOverlayPrefix))]
    [NotifyPropertyChangedFor(nameof(IsUpdateButtonVisible))]
    [NotifyPropertyChangedFor(nameof(UpdateButtonToolTip))]
    [NotifyPropertyChangedFor(nameof(EffectiveUrlPlaceholderText))]
    [NotifyCanExecuteChangedFor(nameof(StartUpdateDownloadCommand))]
    public partial AppUpdateState UpdateState { get; private set; } = AppUpdateState.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateButtonToolTip))]
    public partial string? UpdateVersion { get; private set; }

    [ObservableProperty]
    public partial double UpdateDownloadProgress { get; private set; }

    public bool HasUpdate => UpdateState == AppUpdateState.Available;

    public bool IsUpdateDownloading => UpdateState == AppUpdateState.Downloading;

    public bool IsUpdateDownloaded => UpdateState == AppUpdateState.Downloaded;

    // Only the download blocks user actions; the launch-time check is silent/background, so a slow
    // check must not swallow the one-shot clipboard offer or block downloads on first open.
    public bool IsUpdateBusy => UpdateState == AppUpdateState.Downloading;

    public bool IsInputLocked => IsDownloading || IsUpdateDownloading;

    public bool IsUpdateOverlayVisible =>
        UpdateState is AppUpdateState.Available or AppUpdateState.Downloading
            && string.IsNullOrWhiteSpace(SourceUrl);

    public string EffectiveUrlPlaceholderText =>
        UpdateState is AppUpdateState.Available or AppUpdateState.Downloading
            ? string.Empty
            : "Paste Instagram, X, or YouTube URL";

    public string UpdateOverlayPrefix => UpdateState switch
    {
        AppUpdateState.Available => "Update available",
        AppUpdateState.Downloading => "Downloading...",
        _ => string.Empty,
    };

    public bool IsUpdateButtonVisible =>
        UpdateState is AppUpdateState.Available or AppUpdateState.Downloading or AppUpdateState.Downloaded;

    public string UpdateButtonToolTip => UpdateState switch
    {
        AppUpdateState.Available => string.IsNullOrWhiteSpace(UpdateVersion)
            ? "Download update"
            : $"Download TrimFetch {UpdateVersion}",
        AppUpdateState.Downloading => string.IsNullOrWhiteSpace(UpdateVersion)
            ? "Downloading update"
            : $"Downloading TrimFetch {UpdateVersion}",
        AppUpdateState.Downloaded => string.IsNullOrWhiteSpace(UpdateVersion)
            ? "Install update"
            : $"Install TrimFetch {UpdateVersion}",
        _ => "Update",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideo))]
    [NotifyPropertyChangedFor(nameof(ActiveTrimFileUri))]
    [NotifyCanExecuteChangedFor(nameof(CopyTrimCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyTrimGifCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveTrimCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseTrimCommand))]
    public partial DownloadItem? ActiveTrimItem { get; set; }

    [ObservableProperty]
    public partial bool IsTrimMediaReady { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedHistoryItem))]
    [NotifyCanExecuteChangedFor(nameof(CopyHistoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyHistoryGifCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevealHistoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteHistoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditTrimCommand))]
    public partial DownloadItem? SelectedHistoryItem { get; set; }

    [ObservableProperty]
    public partial double TrimStartSeconds { get; set; }

    [ObservableProperty]
    public partial double TrimEndSeconds { get; set; } = 15;

    [ObservableProperty]
    public partial double TrimPlaybackPositionSeconds { get; set; }

    [ObservableProperty]
    public partial string? CopiedHistoryItemId { get; set; }

    [ObservableProperty]
    public partial bool IsHistoryKeyboardMode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyTrimCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyTrimGifCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveTrimCommand))]
    [NotifyPropertyChangedFor(nameof(IsCopyTrimExporting))]
    [NotifyPropertyChangedFor(nameof(IsCopyTrimGifExporting))]
    [NotifyPropertyChangedFor(nameof(IsSaveTrimExporting))]
    public partial bool IsTrimExporting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCopyTrimExporting))]
    [NotifyPropertyChangedFor(nameof(IsCopyTrimGifExporting))]
    [NotifyPropertyChangedFor(nameof(IsSaveTrimExporting))]
    public partial TrimExportKind TrimExportKind { get; set; }

    [ObservableProperty]
    public partial bool TrimCopySucceeded { get; set; }

    [ObservableProperty]
    public partial bool TrimGifCopySucceeded { get; set; }

    [ObservableProperty]
    public partial bool TrimSaveSucceeded { get; set; }

    public bool IsCopyTrimExporting => IsTrimExporting && TrimExportKind == TrimExportKind.Copy;

    public bool IsCopyTrimGifExporting => IsTrimExporting && TrimExportKind == TrimExportKind.CopyGif;

    public bool IsSaveTrimExporting => IsTrimExporting && TrimExportKind == TrimExportKind.Save;

    public HotKeyShortcut CopyShortcut { get; private set; } = HotKeyAction.Copy.GetDefaultShortcut();

    public HotKeyShortcut CopyGifShortcut { get; private set; } = HotKeyAction.CopyGif.GetDefaultShortcut();

    public HotKeyShortcut CopyHistoryGifShortcut { get; private set; } = HotKeyAction.CopyHistoryGif.GetDefaultShortcut();

    public HotKeyShortcut OpenTrimShortcut { get; private set; } = HotKeyAction.OpenTrim.GetDefaultShortcut();

    public HotKeyShortcut ActivateAppShortcut { get; private set; } = HotKeyAction.ActivateApp.GetDefaultShortcut();

    public ObservableCollection<DownloadItem> History { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> TimelineFramePaths { get; private set; } = [];

    [ObservableProperty]
    public partial double TimelineFrameAspectRatio { get; private set; } = VideoDimensions.Default.AspectRatio;

    [ObservableProperty]
    public partial int TimelineTargetFrameCount { get; private set; } = FilmstripLayout.DefaultFrameCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrimExportFormatLabel))]
    [NotifyPropertyChangedFor(nameof(TrimCopyToolTip))]
    [NotifyPropertyChangedFor(nameof(TrimCopyGifToolTip))]
    [NotifyPropertyChangedFor(nameof(TrimSaveToolTip))]
    [NotifyCanExecuteChangedFor(nameof(CopyTrimGifCommand))]
    [NotifyPropertyChangedFor(nameof(ShowTrimVideoModeToggle))]
    [NotifyPropertyChangedFor(nameof(ShowTrimAudioModeToggle))]
    [NotifyPropertyChangedFor(nameof(ShowTrimModeToggles))]
    public partial bool IsAudioMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTrimVideoModeToggle))]
    [NotifyPropertyChangedFor(nameof(ShowTrimAudioModeToggle))]
    [NotifyPropertyChangedFor(nameof(ShowTrimModeToggles))]
    public partial TrimMediaProfile TrimMediaProfile { get; private set; } = TrimMediaProfile.VideoWithAudio;

    public bool ShowTrimVideoModeToggle => TrimMediaProfile != TrimMediaProfile.AudioOnly;

    public bool ShowTrimAudioModeToggle => TrimMediaProfile != TrimMediaProfile.VideoOnly;

    public bool ShowTrimModeToggles => ShowTrimVideoModeToggle && ShowTrimAudioModeToggle;

    [ObservableProperty]
    public partial float[]? WaveformPeaks { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrimExportFormatLabel))]
    [NotifyPropertyChangedFor(nameof(TrimCopyToolTip))]
    [NotifyPropertyChangedFor(nameof(TrimSaveToolTip))]
    public partial TrimAudioFormat TrimAudioFormat { get; set; } = TrimAudioFormat.Mp3;

    public string TrimExportFormatLabel =>
        IsAudioMode
            ? TrimAudioFormat == TrimAudioFormat.Wav ? "WAV" : "MP3"
            : "MP4";

    public string TrimCopyToolTip =>
        IsAudioMode ? $"Copy trim as {TrimExportFormatLabel}" : "Copy trim";

    public string TrimCopyGifToolTip =>
        $"Copy trim as GIF ({CopyGifShortcut.DisplayText})";

    public string HistoryCopyGifToolTip =>
        $"Copy as GIF ({CopyHistoryGifShortcut.DisplayText})";

    public string TrimSaveToolTip =>
        IsAudioMode ? $"Save trim as {TrimExportFormatLabel}" : "Save trim";

    public string DownloadFolderName => string.IsNullOrWhiteSpace(DownloadFolderPath)
        ? "Downloads"
        : Path.GetFileName(DownloadFolderPath);

    public bool HasHistory => History.Count > 0;

    public bool ShowVideo => ActiveTrimItem is not null;

    public bool ShowDependencySetup => !DependenciesReady;

    public bool IsStatusVisible => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool HasInputPreviewThumbnail =>
        !string.IsNullOrWhiteSpace(InputPreviewThumbnailPath)
        && File.Exists(InputPreviewThumbnailPath);

    public void DismissInputPreviewThumbnail() => InputPreviewThumbnailPath = null;


    public bool HasSelectedHistoryItem => SelectedHistoryItem is not null;

    public string SetupVerifyButtonText => DependenciesReady ? "Continue" : "Check again";

    public string SetupInstallButtonText =>
        !HasYtDlp && !HasFfmpeg
            ? "Install required tools"
            : !HasYtDlp
                ? "Install yt-dlp"
                : "Install ffmpeg";

    public string SetupMissingToolsSummary
    {
        get
        {
            if (DependenciesReady)
            {
                return "All required tools are installed.";
            }

            var missing = new List<string>();
            if (!HasYtDlp)
            {
                missing.Add("yt-dlp");
            }

            if (!HasFfmpeg)
            {
                missing.Add("ffmpeg");
            }

            if (WingetSetupState == DependencySetupItemState.Missing)
            {
                missing.Add("winget (App Installer)");
            }

            return missing.Count == 0
                ? "Checking required tools..."
                : $"Missing: {string.Join(", ", missing)}";
        }
    }

    public string YtDlpSetupStatusText => DescribeSetupItemState(YtDlpSetupState, HasYtDlp);

    public string FfmpegSetupStatusText => DescribeSetupItemState(FfmpegSetupState, HasFfmpeg);

    public string WingetSetupStatusText => DescribeSetupItemState(WingetSetupState, WingetSetupState == DependencySetupItemState.Installed);

    public Uri? ActiveTrimFileUri => ActiveTrimItem is null || !File.Exists(ActiveTrimItem.FilePath)
        ? null
        : new Uri(Path.GetFullPath(ActiveTrimItem.FilePath), UriKind.Absolute);

    public double HistoryPanelHeight => HistoryLayout.ListHeightForItemCount(History.Count);

    public MainPageViewModel()
    {
        History.CollectionChanged += (_, _) =>
        {
            if (_suppressHistoryNotifications)
            {
                return;
            }

            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(HistoryPanelHeight));
        };
    }

    public async Task InitializeAsync()
    {
        await ReloadPreferencesAsync();
        await RefreshDependenciesAsync();
        await ReloadHotKeysAsync();

        ActiveTrimItem = null;
        SelectedHistoryItem = null;
        IsHistoryKeyboardMode = false;

        _ = LoadHistoryAndThumbnailsDeferredAsync();
        _ = CheckForUpdateOnLaunchAsync();
    }

    /// <summary>
    /// Best-effort GitHub release check fired once at startup. Silently no-ops on network
    /// failure or when the local build is already current; only surfaces a chip in the URL
    /// chrome when a real update is available.
    /// </summary>
    public async Task CheckForUpdateOnLaunchAsync()
    {
        var session = Interlocked.Increment(ref _updateCheckSession);
        await UiDispatcher.RunAsync(() =>
        {
            if (UpdateState == AppUpdateState.None)
            {
                UpdateState = AppUpdateState.Checking;
            }
        });

        try
        {
            var currentLabel = AppVersion.GetDisplayLabel();
            var result = await _updateChecker.CheckAsync(currentLabel);

            if (session != _updateCheckSession)
            {
                return;
            }

            await UiDispatcher.RunAsync(() =>
            {
                if (result.Kind == UpdateCheckResultKind.UpToDate
                    || result.DownloadUrl is null
                    || string.IsNullOrWhiteSpace(result.Version))
                {
                    if (UpdateState == AppUpdateState.Checking)
                    {
                        UpdateState = AppUpdateState.None;
                    }

                    return;
                }

                _pendingUpdateResult = result;
                UpdateVersion = result.Version;
                UpdateState = AppUpdateState.Available;
            });
        }
        catch (Exception ex)
        {
            AppDiagnostic.Log($"Update check failed: {ex.Message}");
            await UiDispatcher.RunAsync(() =>
            {
                if (session == _updateCheckSession && UpdateState == AppUpdateState.Checking)
                {
                    UpdateState = AppUpdateState.None;
                }
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartUpdateDownload))]
    private Task StartUpdateDownloadAsync() => UiDispatcher.InvokeAsync(() => StartUpdateDownloadCoreAsync());

    private bool CanStartUpdateDownload() =>
        UpdateState is AppUpdateState.Available or AppUpdateState.Downloaded && !IsDownloadBusy;

    private async Task StartUpdateDownloadCoreAsync()
    {
        // Already on disk (e.g. the user dismissed the install prompt earlier): re-offer
        // the install instead of downloading again.
        if (UpdateState == AppUpdateState.Downloaded && _downloadedUpdate is { } ready)
        {
            await RequestInstallAsync(ready);
            return;
        }

        if (_pendingUpdateResult is not { } result)
        {
            return;
        }

        UpdateState = AppUpdateState.Downloading;
        UpdateDownloadProgress = 0;

        var progress = new Progress<UpdateDownloadProgress>(report =>
        {
            var fraction = report.Percent / 100.0;
            _ = UiDispatcher.RunAsync(() =>
            {
                UpdateDownloadProgress = Math.Max(UpdateDownloadProgress, fraction);
                DownloadProgressChanged?.Invoke(fraction);
            });
        });

        DownloadedUpdate downloaded;
        try
        {
            downloaded = await _updateChecker.DownloadAsync(result, progress);
        }
        catch (Exception ex)
        {
            AppDiagnostic.LogException("Update download", ex);
            UpdateState = AppUpdateState.Available;
            UpdateDownloadProgress = 0;
            SetStatus($"Update download failed: {ex.Message}");
            return;
        }

        _downloadedUpdate = downloaded;
        _pendingUpdateResult = null;
        UpdateState = AppUpdateState.Downloaded;
        await RequestInstallAsync(downloaded);
    }

    /// <summary>
    /// Hands off to the UI to launch the installer and exit. If the launch fails the state
    /// stays <see cref="AppUpdateState.Downloaded"/>, so the update button remains live and
    /// the install can be retried with one click.
    /// </summary>
    private async Task RequestInstallAsync(DownloadedUpdate update)
    {
        if (UpdateInstallRequested is null)
        {
            return;
        }

        // When the handler returns true the app is already shutting down, so there is
        // nothing more to do here.
        await UpdateInstallRequested(update);
    }

    private async Task LoadHistoryAndThumbnailsDeferredAsync()
    {
        IReadOnlyList<DownloadItem> items;
        try
        {
            items = await _storage.LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            AppDiagnostic.LogException("LoadHistory", ex);
            return;
        }

        await UiDispatcher.RunAsync(() =>
        {
            _suppressHistoryNotifications = true;
            try
            {
                History.Clear();
                foreach (var item in items)
                {
                    History.Add(item);
                }
            }
            finally
            {
                _suppressHistoryNotifications = false;
                OnPropertyChanged(nameof(HasHistory));
                OnPropertyChanged(nameof(HistoryPanelHeight));
            }
        });

        foreach (var item in items)
        {
            _ = EnsureThumbnailAsync(item);
        }
    }

    public async Task ReloadPreferencesAsync()
    {
        DownloadFolderPath = await _preferences.LoadDownloadFolderAsync();
        _downloadQuality = await _preferences.GetDownloadQualityAsync();
        AutoDownloadClipboardUrls = await _preferences.GetAutoDownloadClipboardUrlsAsync();
        Directory.CreateDirectory(DownloadFolderPath);
        OnPropertyChanged(nameof(DownloadFolderName));
    }

    public async Task ReloadHotKeysAsync()
    {
        CopyShortcut = await _preferences.GetHotKeyAsync(HotKeyAction.Copy);
        CopyGifShortcut = await _preferences.GetHotKeyAsync(HotKeyAction.CopyGif);
        CopyHistoryGifShortcut = await _preferences.GetHotKeyAsync(HotKeyAction.CopyHistoryGif);
        OpenTrimShortcut = await _preferences.GetHotKeyAsync(HotKeyAction.OpenTrim);
        ActivateAppShortcut = await _preferences.GetHotKeyAsync(HotKeyAction.ActivateApp);
        OnPropertyChanged(nameof(CopyShortcut));
        OnPropertyChanged(nameof(CopyGifShortcut));
        OnPropertyChanged(nameof(CopyHistoryGifShortcut));
        OnPropertyChanged(nameof(TrimCopyGifToolTip));
        OnPropertyChanged(nameof(HistoryCopyGifToolTip));
        OnPropertyChanged(nameof(OpenTrimShortcut));
        OnPropertyChanged(nameof(ActivateAppShortcut));
    }

    /// <summary>When set, returns the live trim range from the timeline UI (authoritative for export).</summary>
    public Func<TrimSelection>? GetTrimSelectionFromView { get; set; }

    public PreferencesService Preferences => _preferences;

    public async Task OfferClipboardUrlAsync()
    {
        if (IsDownloadBusy || IsUpdateBusy || IsTrimExporting || ShowDependencySetup)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(SourceUrl.Trim()))
        {
            return;
        }

        string? url = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(attempt switch
                {
                    1 => 80,
                    2 => 120,
                    3 => 160,
                    _ => 200,
                });
            }

            if (!string.IsNullOrWhiteSpace(SourceUrl.Trim()))
            {
                return;
            }

            var clipboardText = await ClipboardUrlReader.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(clipboardText)
                || !ClipboardUrlReader.TryExtractHttpUrl(clipboardText, out var candidate))
            {
                continue;
            }

            if (!YtDlpSupportedHostIndex.IsSupportedUrl(candidate))
            {
                return;
            }

            url = candidate;
            break;
        }

        if (url is null)
        {
            return;
        }

        var fingerprint = MediaUrlNormalizer.GetMatchKey(url);
        if (string.Equals(fingerprint, _lastClipboardOfferFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _lastClipboardOfferFingerprint = fingerprint;

        var existing = FindDownloadedHistoryItemInternal(url);
        if (existing is not null)
        {
            await UiDispatcher.RunAsync(() => FocusExistingHistoryItemInternal(existing, url));
            return;
        }

        if (!AutoDownloadClipboardUrls)
        {
            await UiDispatcher.RunAsync(() =>
            {
                SourceUrl = url;
                ClearStatus();
            });
            return;
        }

        if (ClipboardLaunchOrchestratorAsync is not null)
        {
            await ClipboardLaunchOrchestratorAsync(url);
            return;
        }

        await StartDownloadFromUrlAsync(url);
    }

    public async Task TryCommitSourceUrlAsync()
    {
        if (SuppressUrlAutoDownload || IsDownloadBusy || IsUpdateBusy || IsTrimExporting || ShowDependencySetup)
        {
            return;
        }

        var candidate = SourceUrl.Trim();
        if (!string.Equals(candidate, SourceUrl, StringComparison.Ordinal))
        {
            return;
        }

        await TryCommitUrlAsync(candidate, updateSourceField: false);
    }

    public async Task CommitSourceUrlOnEnterAsync()
    {
        if (IsDownloadBusy || IsUpdateBusy || IsTrimExporting || ShowDependencySetup)
        {
            return;
        }

        var candidate = SourceUrl.Trim();
        if (!LooksLikeWebUrl(candidate))
        {
            SetStatus(UserFacingMessages.InvalidUrl);
            return;
        }

        if (!YtDlpSupportedHostIndex.IsSupportedUrl(candidate))
        {
            SetStatus(UserFacingMessages.UnsupportedHost);
            return;
        }

        if (!IsSubmittableUrl(candidate))
        {
            SetStatus(UserFacingMessages.IncompleteUrl);
            return;
        }

        var existing = FindDownloadedHistoryItemInternal(candidate);
        if (existing is not null)
        {
            await UiDispatcher.RunAsync(() => OpenExistingHistoryItem(existing));
            return;
        }

        await TryCommitUrlAsync(candidate, updateSourceField: false);
    }

    public void PrepareForWindowHidden()
    {
        if (IsDownloadBusy || IsTrimExporting)
        {
            return;
        }

        _lastClipboardOfferFingerprint = null;
        SourceUrl = string.Empty;
    }

    public bool IsDownloadBusy => IsDownloading || IsFileDownloading;

    public bool TryBeginDownload(string url)
    {
        if (IsDownloadBusy
            || IsUpdateBusy
            || string.Equals(_inFlightDownloadUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _inFlightDownloadUrl = url;
        return true;
    }

    [RelayCommand]
    private async Task CopyInstallPromptAsync()
    {
        await _clipboard.CopyTextAsync(DependencySetupService.InstallPrompt);
        SetStatus("Install prompt copied.");
    }

    [RelayCommand(CanExecute = nameof(CanInstallDependencies))]
    private async Task InstallDependenciesAsync()
    {
        IsInstallingDependencies = true;
        SetupProgressMessage = UserFacingMessages.InstallingTools;
        try
        {
            var status = await _dependencySetup.CheckAsync();
            SyncSetupItemStatesFromStatus(status);
            if (status.IsReady)
            {
                await RefreshDependenciesAsync(force: true);
                SetupProgressMessage = string.Empty;
                return;
            }

            var progress = new Progress<DependencySetupProgressReport>(ApplySetupProgress);
            await _dependencySetup.InstallMissingAsync(status, progress);
            await RefreshDependenciesAsync(force: true);

            SetupProgressMessage = DependenciesReady
                ? string.Empty
                : UserFacingMessages.SetupNeeded;
        }
        catch (Exception ex)
        {
            SetupProgressMessage = ex.Message;
            await RefreshDependenciesAsync(force: true);
        }
        finally
        {
            IsInstallingDependencies = false;
            if (DependenciesReady)
            {
                SetupProgressMessage = string.Empty;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRecheckDependencies))]
    private async Task RecheckDependenciesAsync()
    {
        IsInstallingDependencies = true;
        try
        {
            await RefreshDependenciesAsync(force: true);
            SetupProgressMessage = DependenciesReady
                ? string.Empty
                : UserFacingMessages.SetupNeeded;
        }
        finally
        {
            IsInstallingDependencies = false;
        }
    }

    private bool CanInstallDependencies() => !IsInstallingDependencies && !DependenciesReady;

    private bool CanRecheckDependencies() => !IsInstallingDependencies;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task DownloadAsync() => UiDispatcher.InvokeAsync(() => DownloadAsyncCore());

    public Task StartDownloadFromUrlAsync(string url) =>
        UiDispatcher.InvokeAsync(async () => await DownloadAsyncCore(url));

    public DownloadItem? FindDownloadedHistoryItem(string url) =>
        FindDownloadedHistoryItemInternal(url);

    public void FocusExistingHistoryItem(DownloadItem item, string url) =>
        FocusExistingHistoryItemInternal(item, url);

    private async Task DownloadAsyncCore(string? explicitUrl = null)
    {
        var url = (explicitUrl ?? SourceUrl).Trim();
        if (!LooksLikeWebUrl(url))
        {
            SetStatus(UserFacingMessages.InvalidUrl);
            return;
        }

        if (!TryBeginDownload(url))
        {
            return;
        }

        var session = Interlocked.Increment(ref _downloadSession);
        CancellationToken cancellationToken = default;
        Action<DownloadProgressUpdate> onProgress = OnPipelineProgress;

        try
        {
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
            _downloadCts = new CancellationTokenSource();
            cancellationToken = _downloadCts.Token;

            UiDispatcher.Run(() =>
            {
                IsDownloading = true;
                IsFileDownloading = false;
                DownloadProgress = 0;
                _downloadStagesCompleted = 0;
                _lastDownloadFraction = 0;
                _lastPostProcessFraction = 0;
                _downloadSegmentHighWater = 0;
                _pendingDownloadProgress = 0;
                _downloadProgressDispatchQueued = false;
                DownloadTrimReadyForReveal = false;
                InputPreviewThumbnailPath = null;
                StatusMessage = string.Empty;
            });

            ReportPipelineProgress(0.04);
            await RefreshDependenciesAsync();
            if (!DependenciesReady)
            {
                SetStatus(UserFacingMessages.SetupNeeded);
                return;
            }

            if (IsSessionCancelled(session, cancellationToken))
            {
                return;
            }

            ReportPipelineProgress(0.08);

            UiDispatcher.Run(() =>
            {
                IsFileDownloading = true;
                DownloadProgress = Math.Max(DownloadProgress, 0.08);
            });

            var downloadQuality = MediaSourceAnalyzer.ResolveDownloadQuality(url, _downloadQuality);
            var result = await _downloader.DownloadAsync(
                url,
                DownloadFolderPath,
                downloadQuality,
                onProgress,
                cancellationToken);
            if (IsSessionCancelled(session, cancellationToken))
            {
                return;
            }

            var item = new DownloadItem
            {
                SourceUrl = url,
                Title = result.Title,
                FilePath = result.FilePath,
                CreatedAt = DateTimeOffset.Now,
            };

            if (IsSessionCancelled(session, cancellationToken))
            {
                return;
            }

            _ = EnsureThumbnailAsync(item);

            // Lock the bar at the post-process ceiling while hidden trim prep runs — never sprint to ~99% early.
            UiDispatcher.Run(() =>
                ReportPipelineProgress(DownloadPipelineProgress.FromPostProcessPhase(1)));

            var prewarmTask = PrewarmTrimMediaProfileAsync(item);
            var prepTask = StartDownloadTrimPrepAsync is not null
                ? StartDownloadTrimPrepAsync(item)
                : Task.FromResult(-1);
            await Task.WhenAll(prewarmTask, prepTask);
            if (IsSessionCancelled(session, cancellationToken))
            {
                return;
            }

            if (IsSessionCancelled(session, cancellationToken))
            {
                return;
            }

            if (CompleteDownloadWithTrimRevealAsync is null)
            {
                SetStatus(UserFacingMessages.DownloadFailed);
                return;
            }

            var trimRevealFailed = false;
            try
            {
                await CompleteDownloadWithTrimRevealAsync(item);
            }
            catch (Exception ex)
            {
                AppDiagnostic.LogException("Download trim reveal", ex);
                trimRevealFailed = true;
                SetStatus(UserFacingMessages.DownloadFailed);
            }

            await UiDispatcher.InvokeAsync(async () =>
            {
                if (trimRevealFailed)
                {
                    if (DownloadCompletionUiAsync is not null)
                    {
                        await DownloadCompletionUiAsync(item);
                    }
                    else
                    {
                        InsertHistoryItem(item);
                    }
                }

                IsDownloading = false;
                if (!trimRevealFailed)
                {
                    ClearStatus();
                }
            });

            _ = _clipboard.CopyFileAsync(item.FilePath);
            _ = PublishDownloadCompletionAsync(item);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("not found in PATH", StringComparison.OrdinalIgnoreCase))
            {
                ExternalToolLocator.Invalidate();
                await RefreshDependenciesAsync(force: true);
            }

            if (!IsSessionCancelled(session, cancellationToken))
            {
                var message = UserFacingMessages.FromException(ex);
                await UiDispatcher.RunAsync(() => SetStatus(message));
            }
        }
        finally
        {
            if (session == _downloadSession)
            {
                await UiDispatcher.RunAsync(() =>
                {
                    if (IsFileDownloading || IsDownloading)
                    {
                        IsFileDownloading = false;
                        DownloadProgress = 0;
                        IsDownloading = false;
                        _inFlightDownloadUrl = null;
                        InputPreviewThumbnailPath = null;
                        AppServices.Tray.SetDownloading(false);
                    }
                });

                _downloadCts?.Dispose();
                _downloadCts = null;
            }
        }
    }

    private bool IsSessionCancelled(int session, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested || session != _downloadSession;

    private async Task<string?> FetchAndShowPreviewThumbnailAsync(
        string url,
        int session,
        CancellationToken cancellationToken)
    {
        var previewThumbnail = await _thumbnailFetch.FetchThumbnailAsync(url, cancellationToken);
        if (string.IsNullOrWhiteSpace(previewThumbnail) || IsSessionCancelled(session, cancellationToken))
        {
            return previewThumbnail;
        }

        await UiDispatcher.RunAsync(() =>
        {
            InputPreviewThumbnailPath = previewThumbnail;
            _ = ImageFileSource.FromPath(previewThumbnail, 960);
        });
        return previewThumbnail;
    }

    public void InsertHistoryItem(DownloadItem item)
    {
        History.Insert(0, item);
        ScheduleSaveHistory();
    }

    private void OnPipelineProgress(DownloadProgressUpdate update)
    {
        double overall;
        if (update.Phase == DownloadProgressPhase.Download)
        {
            if (update.Fraction < 0.1 && _lastDownloadFraction > 0.9)
            {
                _downloadStagesCompleted++;
            }

            _lastDownloadFraction = update.Fraction;
            var segment = Math.Min(1, (_downloadStagesCompleted + update.Fraction) / (_downloadStagesCompleted + 1));
            _downloadSegmentHighWater = Math.Max(_downloadSegmentHighWater, segment);
            overall = DownloadPipelineProgress.FromDownloadHighWater(_downloadSegmentHighWater);
        }
        else
        {
            _lastPostProcessFraction = Math.Max(_lastPostProcessFraction, update.Fraction);
            overall = DownloadPipelineProgress.FromPostProcessPhase(_lastPostProcessFraction);
        }

        QueueDownloadProgressReport(overall);
    }

    /// <summary>Hidden trim prep finished; bar may reach 100% and play entrance.</summary>
    public bool DownloadTrimReadyForReveal { get; private set; }

    public void SetDownloadTrimReadyForReveal(bool ready) => DownloadTrimReadyForReveal = ready;

    public void CompleteDownloadPipelineVisuals()
    {
        IsFileDownloading = false;
        DownloadProgress = 0;
        _inFlightDownloadUrl = null;
    }

    public void ReportPipelineProgress(double overall) =>
        QueueDownloadProgressReport(Math.Clamp(overall, 0, 1));

    public void ReportMediaPrepProgress(double prepFraction) =>
        ReportPipelineProgress(DownloadPipelineProgress.FromTrimPrepPhase(prepFraction));

    private void QueueDownloadProgressReport(double overall)
    {
        // Everything below the reveal is clamped to PreRevealMax so the ring can creep through
        // the hidden trim prep (ffprobe + media open) instead of freezing at PostProcessEnd;
        // only the reveal sequence reports an explicit 1.0.
        if (overall < 1)
        {
            overall = Math.Min(overall, DownloadPipelineProgress.PreRevealMax);
        }

        _pendingDownloadProgress = Math.Max(_pendingDownloadProgress, overall);
        if (_downloadProgressDispatchQueued)
        {
            return;
        }

        _downloadProgressDispatchQueued = true;
        UiDispatcher.Run(() =>
        {
            _downloadProgressDispatchQueued = false;
            var value = _pendingDownloadProgress;
            ReportDownloadProgress(Math.Max(DownloadProgress, value));
        });
    }

    private void ReportDownloadProgress(double value)
    {
        DownloadProgress = value;
        DownloadProgressChanged?.Invoke(value);
        AppServices.Tray.SetDownloading(IsDownloadBusy);
    }

    private async Task PublishDownloadCompletionAsync(DownloadItem item)
    {
        var previewPath = InputPreviewThumbnailPath;

        try
        {
            await AppServices.Notifications.ShowDownloadCompletedAsync(item, previewPath);
        }
        catch (Exception ex)
        {
            AppDiagnostic.Log($"Download completion notification failed: {ex.Message}");
        }
        finally
        {
            AppServices.Tray.SetDownloading(IsDownloadBusy);
            await UiDispatcher.RunAsync(() => InputPreviewThumbnailPath = null);
        }
    }

    partial void OnSourceUrlChanged(string value)
    {
        UiDispatcher.Run(() =>
        {
            if (!IsDownloadBusy && !string.IsNullOrWhiteSpace(InputPreviewThumbnailPath))
            {
                _thumbnailFetch.TryDeletePreview(InputPreviewThumbnailPath);
                InputPreviewThumbnailPath = null;
            }
        });
    }

    private bool CanDownload() => !IsDownloadBusy && !IsUpdateBusy && DependenciesReady && !string.IsNullOrWhiteSpace(SourceUrl);

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var folder = await _filePicker.PickFolderAsync();
        if (folder is null)
        {
            return;
        }

        DownloadFolderPath = folder;
        Directory.CreateDirectory(DownloadFolderPath);
        await _preferences.SaveDownloadFolderAsync(folder);
        ClearStatus();
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        History.Clear();
        ActiveTrimItem = null;
        SelectedHistoryItem = null;
        IsHistoryKeyboardMode = false;
        ImageFileSource.ClearCache();
        await SaveHistoryAsync();
        ClearStatus();
    }

    [RelayCommand]
    private void CloseTrim() => ActiveTrimItem = null;

    [RelayCommand(CanExecute = nameof(HasSelectedHistoryItem))]
    private void EditTrim()
    {
        if (SelectedHistoryItem is not null)
        {
            ActiveTrimItem = SelectedHistoryItem;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedHistoryItem))]
    private async Task CopyHistoryAsync()
    {
        if (SelectedHistoryItem is null)
        {
            return;
        }

        await _clipboard.CopyFileAsync(SelectedHistoryItem.FilePath);
        MarkHistoryCopied(SelectedHistoryItem.Id);
        SetStatus($"Copied {SelectedHistoryItem.DisplayName}.");
    }

    [RelayCommand(CanExecute = nameof(CanCopyHistoryGif))]
    private async Task CopyHistoryGifAsync()
    {
        if (SelectedHistoryItem is null)
        {
            return;
        }

        await ExportHistoryItemAsGifAsync(SelectedHistoryItem);
    }

    private bool CanCopyHistoryGif() =>
        HasSelectedHistoryItem
        && SelectedHistoryItem!.FileExists
        && !IsTrimExporting
        && !SelectedHistoryItem.IsGifExporting;

    [RelayCommand(CanExecute = nameof(HasSelectedHistoryItem))]
    private void RevealHistory()
    {
        try
        {
            if (SelectedHistoryItem is not null)
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{SelectedHistoryItem.FilePath}\"");
            }
        }
        catch
        {
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedHistoryItem))]
    private void OpenSource()
    {
        try
        {
            if (SelectedHistoryItem is not null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SelectedHistoryItem.SourceUrl)
                {
                    UseShellExecute = true,
                });
            }
        }
        catch
        {
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedHistoryItem))]
    private async Task DeleteHistoryAsync()
    {
        if (SelectedHistoryItem is null)
        {
            return;
        }

        var removed = SelectedHistoryItem;
        var index = History.IndexOf(removed);
        History.Remove(removed);

        if (ActiveTrimItem?.Id == removed.Id)
        {
            ActiveTrimItem = null;
        }

        SelectedHistoryItem = History.ElementAtOrDefault(Math.Clamp(index, 0, Math.Max(History.Count - 1, 0)));
        if (History.Count == 0)
        {
            IsHistoryKeyboardMode = false;
        }

        await SaveHistoryAsync();
    }

    public void MarkHistoryCopied(string id)
    {
        CopiedHistoryItemId = id;
        if (History.FirstOrDefault(item => item.Id == id) is { } copiedItem)
        {
            copiedItem.IsCopied = true;
        }

        _ = ClearCopiedIndicatorAsync(id);
    }

    public void MarkHistoryGifCopied(string id)
    {
        if (History.FirstOrDefault(item => item.Id == id) is { } copiedItem)
        {
            copiedItem.IsGifCopied = true;
            _ = ClearHistoryGifCopiedIndicatorAsync(id);
        }
    }

    private async Task ExportHistoryItemAsGifAsync(DownloadItem item)
    {
        if (!item.FileExists)
        {
            SetStatus("GIF export failed.");
            return;
        }

        var duration = await GetOrProbeDurationAsync(item.FilePath);
        if (duration <= 0)
        {
            SetStatus("GIF export failed.");
            return;
        }

        var exportSeconds = Math.Min(duration, MaxHistoryGifSeconds);
        var selection = new TrimSelection(0, exportSeconds);

        await UiDispatcher.RunAsync(() =>
        {
            item.IsGifExporting = true;
            CopyHistoryGifCommand.NotifyCanExecuteChanged();
            CopyTrimGifCommand.NotifyCanExecuteChanged();
        });

        try
        {
            var outputPath = _trimExporter.CreateTemporaryGifTrimPath();
            var gifPath = await _trimExporter.ExportGifTrimAsync(
                item.FilePath,
                selection,
                outputPath);

            await _clipboard.CopyFileAsync(gifPath);

            await UiDispatcher.RunAsync(() => MarkHistoryGifCopied(item.Id));

            SetStatus(duration > MaxHistoryGifSeconds
                ? $"GIF copied (first {MaxHistoryGifSeconds:0} s)."
                : "GIF copied.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CopyHistoryGif failed: {ex}");
            SetStatus("GIF export failed.");
        }
        finally
        {
            await UiDispatcher.RunAsync(() =>
            {
                item.IsGifExporting = false;
                CopyHistoryGifCommand.NotifyCanExecuteChanged();
                CopyTrimGifCommand.NotifyCanExecuteChanged();
            });
        }
    }

    public void SelectHistoryByIndex(int index)
    {
        if (History.Count == 0)
        {
            ClearHistoryNavigationHighlights();
            SelectedHistoryItem = null;
            return;
        }

        index = Math.Clamp(index, 0, History.Count - 1);
        var selected = History[index];
        ClearHistoryNavigationHighlights();
        SelectedHistoryItem = selected;

        if (IsHistoryKeyboardMode)
        {
            selected.IsNavigationHighlighted = true;
        }

        HistoryListScrollTarget = selected;
        OnPropertyChanged(nameof(HistoryListScrollTarget));
    }

    public void ClearHistoryNavigationHighlights()
    {
        foreach (var item in History)
        {
            item.IsNavigationHighlighted = false;
        }
    }

    public void SetHistoryPointerHighlight(DownloadItem? item)
    {
        foreach (var entry in History)
        {
            entry.IsPointerHighlighted = ReferenceEquals(entry, item);
        }
    }

    public void ClearHistoryPointerHighlights()
    {
        foreach (var item in History)
        {
            item.IsPointerHighlighted = false;
        }
    }

    partial void OnIsHistoryKeyboardModeChanged(bool value)
    {
        if (!value)
        {
            ClearHistoryNavigationHighlights();
            ClearHistoryPointerHighlights();
        }
    }

    public DownloadItem? HistoryListScrollTarget { get; private set; }

    [RelayCommand(CanExecute = nameof(CanExportTrim))]
    private async Task CopyTrimAsync()
    {
        if (ActiveTrimItem is null || IsTrimExporting)
        {
            return;
        }

        IsTrimExporting = true;
        TrimExportKind = TrimExportKind.Copy;
        TrimCopySucceeded = false;
        TrimGifCopySucceeded = false;
        TrimSaveSucceeded = false;

        try
        {
            var selection = CurrentTrimSelection();
            string trimmedPath;
            if (IsAudioMode)
            {
                var outputPath = _trimExporter.CreateTemporaryAudioTrimPath(TrimAudioFormat);
                trimmedPath = await _trimExporter.ExportAudioTrimAsync(
                    ActiveTrimItem.FilePath,
                    selection,
                    outputPath,
                    TrimAudioFormat);
            }
            else
            {
                var outputPath = _trimExporter.CreateTemporaryTrimPath(ActiveTrimItem.FilePath);
                trimmedPath = await _trimExporter.ExportTrimAsync(
                    ActiveTrimItem.FilePath,
                    selection,
                    outputPath);
            }

            await _clipboard.CopyFileAsync(trimmedPath);

            await UiDispatcher.RunAsync(() =>
            {
                TrimCopySucceeded = true;
                _ = ClearTrimCopyIndicatorAsync();
            });

            SetStatus("Trim copied.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CopyTrim failed: {ex}");
            SetStatus("Trim failed.");
        }
        finally
        {
            await UiDispatcher.RunAsync(() =>
            {
                IsTrimExporting = false;
                TrimExportKind = TrimExportKind.None;
            });
        }
    }

    private bool CanExportTrim() => ShowVideo && !IsTrimExporting;

    private bool CanExportTrimGif() => ShowVideo && !IsTrimExporting && !IsAudioMode;

    [RelayCommand(CanExecute = nameof(CanExportTrimGif))]
    private async Task CopyTrimGifAsync()
    {
        if (ActiveTrimItem is null || IsTrimExporting || IsAudioMode)
        {
            return;
        }

        IsTrimExporting = true;
        TrimExportKind = TrimExportKind.CopyGif;
        TrimGifCopySucceeded = false;
        TrimCopySucceeded = false;
        TrimSaveSucceeded = false;

        try
        {
            var selection = CurrentTrimSelection();
            var outputPath = _trimExporter.CreateTemporaryGifTrimPath();
            var gifPath = await _trimExporter.ExportGifTrimAsync(
                ActiveTrimItem.FilePath,
                selection,
                outputPath);

            await _clipboard.CopyFileAsync(gifPath);

            await UiDispatcher.RunAsync(() =>
            {
                TrimGifCopySucceeded = true;
                _ = ClearTrimGifCopyIndicatorAsync();
            });

            SetStatus("GIF copied.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CopyTrimGif failed: {ex}");
            SetStatus("GIF export failed.");
        }
        finally
        {
            await UiDispatcher.RunAsync(() =>
            {
                IsTrimExporting = false;
                TrimExportKind = TrimExportKind.None;
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportTrim))]
    private async Task SaveTrimAsync()
    {
        if (ActiveTrimItem is null || IsTrimExporting)
        {
            return;
        }

        IsTrimExporting = true;
        TrimExportKind = TrimExportKind.Save;
        TrimSaveSucceeded = false;
        TrimCopySucceeded = false;
        TrimGifCopySucceeded = false;

        try
        {
            string trimmedPath;
            if (IsAudioMode)
            {
                var selection = CurrentTrimSelection();
                var defaultPath = _trimExporter.CreateDefaultAudioTrimPath(
                    ActiveTrimItem.FilePath,
                    selection,
                    TrimAudioFormat);
                var extension = TrimAudioFormat == TrimAudioFormat.Wav ? ".wav" : ".mp3";
                var filterName = TrimAudioFormat == TrimAudioFormat.Wav ? "WAV audio" : "MP3 audio";
                var savePath = await _filePicker.PickSaveFileAsync(
                        Path.GetFileNameWithoutExtension(defaultPath),
                        extension,
                        filterName)
                    ?? defaultPath;
                trimmedPath = await _trimExporter.ExportAudioTrimAsync(
                    ActiveTrimItem.FilePath,
                    selection,
                    savePath,
                    TrimAudioFormat);
            }
            else
            {
                var defaultPath = _trimExporter.CreateDefaultTrimPath(ActiveTrimItem.FilePath, CurrentTrimSelection());
                var savePath = await _filePicker.PickSaveFileAsync(
                        Path.GetFileNameWithoutExtension(defaultPath),
                        ".mp4",
                        "MP4 video")
                    ?? defaultPath;
                trimmedPath = await _trimExporter.ExportTrimAsync(
                    ActiveTrimItem.FilePath,
                    CurrentTrimSelection(),
                    savePath);
            }

            await UiDispatcher.RunAsync(() =>
            {
                TrimSaveSucceeded = true;
                _ = ClearTrimSaveIndicatorAsync();
            });

            SetStatus($"Saved {Path.GetFileName(trimmedPath)}.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SaveTrim failed: {ex}");
            SetStatus("Trim failed.");
        }
        finally
        {
            await UiDispatcher.RunAsync(() =>
            {
                IsTrimExporting = false;
                TrimExportKind = TrimExportKind.None;
            });
        }
    }

    partial void OnActiveTrimItemChanged(DownloadItem? value)
    {
        var previousId = _timelineStripItemId;
        var itemChanged = value?.Id != previousId;
        _timelineStripItemId = value?.Id;

        if (value is null)
        {
            IsTrimMediaReady = false;
        }

        if (itemChanged && !string.IsNullOrEmpty(previousId))
        {
            CancelTimelineStripWork(previousId);
        }

        if (itemChanged)
        {
            if (value is null)
            {
                TimelineFramePaths = [];
                TimelineFrameAspectRatio = VideoDimensions.Default.AspectRatio;
                TimelineTargetFrameCount = FilmstripLayout.DefaultFrameCount;
                _lastPublishedTimelineFrameCount = 0;
            }
            else if (!TryApplyCachedTimelineStrip(value))
            {
                TimelineTargetFrameCount = FilmstripLayout.DefaultFrameCount;
                _lastPublishedTimelineFrameCount = 0;
                _lastTimelinePublishUtc = DateTimeOffset.MinValue;
            }
        }
        TrimStartSeconds = 0;
        TrimEndSeconds = 15;
        TrimCopySucceeded = false;
        TrimGifCopySucceeded = false;
        TrimSaveSucceeded = false;
        TrimExportKind = TrimExportKind.None;
        WaveformPeaks = null;

        if (value is null)
        {
            SetTrimMediaProfile(TrimMediaProfile.VideoWithAudio);
            IsAudioMode = false;
        }
        else
        {
            SetTrimMediaProfile(MediaSourceAnalyzer.SuggestProfileFromUrl(value.SourceUrl));
            _ = EnsureTrimMediaProfileAsync(value);
            _ = EnsureThumbnailAsync(value);
        }
    }

    partial void OnTrimMediaProfileChanged(TrimMediaProfile value) =>
        SyncAudioModeToProfile(value);

    private void SetTrimMediaProfile(TrimMediaProfile profile)
    {
        if (TrimMediaProfile == profile)
        {
            SyncAudioModeToProfile(profile);
            return;
        }

        TrimMediaProfile = profile;
    }

    private void SyncAudioModeToProfile(TrimMediaProfile profile)
    {
        var audio = profile == TrimMediaProfile.AudioOnly;
        if (IsAudioMode != audio)
        {
            IsAudioMode = audio;
        }
    }

    /// <summary>
    /// Probes the media profile ahead of the reveal and caches it (keyed by file path) so the reveal's
    /// own <see cref="EnsureTrimMediaProfileAsync"/> resolves instantly instead of spawning ffprobe.
    /// </summary>
    private async Task PrewarmTrimMediaProfileAsync(DownloadItem item)
    {
        if (!item.FileExists)
        {
            return;
        }

        try
        {
            var profile = await MediaProbeService.GetTrimMediaProfileAsync(item.FilePath, item.SourceUrl);
            _prewarmedTrimProfile = (item.FilePath, profile);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Trim media profile prewarm failed: {ex.Message}");
        }
    }

    public async Task EnsureTrimMediaProfileAsync(DownloadItem item)
    {
        if (!item.FileExists)
        {
            return;
        }

        var requestId = item.Id;

        // Fast path: the download pipeline already probed this exact file, so apply it without ffprobe.
        if (_prewarmedTrimProfile is { } warm
            && string.Equals(warm.Path, item.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            await UiDispatcher.RunAsync(() =>
            {
                if (ActiveTrimItem?.Id == requestId)
                {
                    SetTrimMediaProfile(warm.Profile);
                }
            });
            return;
        }

        try
        {
            var profile = await MediaProbeService.GetTrimMediaProfileAsync(item.FilePath, item.SourceUrl);
            if (ActiveTrimItem?.Id != requestId)
            {
                return;
            }

            await UiDispatcher.RunAsync(() =>
            {
                if (ActiveTrimItem?.Id != requestId)
                {
                    return;
                }

                SetTrimMediaProfile(profile);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Trim media profile probe failed: {ex.Message}");
        }
    }

    public async Task<bool> EnsureTrimDurationFromProbeAsync(DownloadItem item)
    {
        var duration = await GetOrProbeDurationAsync(item.FilePath);
        if (duration <= 0 || ActiveTrimItem?.Id != item.Id)
        {
            return false;
        }

        await UiDispatcher.RunAsync(() =>
        {
            if (ActiveTrimItem?.Id != item.Id)
            {
                return;
            }

            MediaDurationSeconds = duration;
            TrimEndSeconds = duration;
            RememberTimelineDuration(item, duration);
        });

        return true;
    }

    partial void OnIsAudioModeChanged(bool value)
    {
        if (!value)
        {
            return;
        }

        if (ActiveTrimItem is { } item)
        {
            _ = EnsureWaveformPeaksAsync(item);
        }
    }

    public Task EnsureWaveformPeaksAsync(DownloadItem item)
    {
        if (!item.FileExists || !IsAudioMode)
        {
            return Task.CompletedTask;
        }

        return RunWaveformPeaksAsync(item);
    }

    private async Task RunWaveformPeaksAsync(DownloadItem item)
    {
        try
        {
            var requestId = item.Id;
            var duration = await GetOrProbeDurationAsync(item.FilePath);
            if (duration <= 0 || ActiveTrimItem?.Id != requestId || !IsAudioMode)
            {
                return;
            }

            var columnCount = FilmstripLayout.ClampFrameCount(
                Math.Max(
                    TimelineTargetFrameCount,
                    FilmstripLayout.CalculateFrameCount(FilmstripLayout.DefaultFilmstripWidth, 1.0)));

            var cached = _waveformStrip.ReadCachedPeaks(item.Id, duration, columnCount);
            if (cached is not null)
            {
                await UiDispatcher.RunAsync(() =>
                {
                    if (ActiveTrimItem?.Id == requestId && IsAudioMode)
                    {
                        WaveformPeaks = cached;
                    }
                });
                return;
            }

            if (!ExternalToolLocator.IsFfmpegAvailable)
            {
                return;
            }

            var peaks = await _waveformStrip.GetOrGeneratePeaksAsync(
                item.FilePath,
                item.Id,
                duration,
                columnCount);

            await UiDispatcher.RunAsync(() =>
            {
                if (ActiveTrimItem?.Id == requestId && IsAudioMode)
                {
                    WaveformPeaks = peaks;
                }
            });
        }
        catch (Exception ex)
        {
            AppDiagnostic.Log($"Waveform peaks failed: {ex.Message}");
        }
    }

    partial void OnMediaDurationSecondsChanged(double value)
    {
        if (!double.IsFinite(value) || value <= 0 || ActiveTrimItem is null)
        {
            return;
        }

        TrimStartSeconds = 0;
        TrimEndSeconds = value;
    }

    public void RememberTimelineDuration(DownloadItem item, double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 || !item.FileExists)
        {
            return;
        }

        _probedDurations[Path.GetFullPath(item.FilePath)] = durationSeconds;

        if (!UiDispatcher.HasThreadAccess)
        {
            UiDispatcher.Run(() => ApplyRememberedTimelineDuration(item, durationSeconds));
            return;
        }

        ApplyRememberedTimelineDuration(item, durationSeconds);
    }

    private void ApplyRememberedTimelineDuration(DownloadItem item, double durationSeconds)
    {
        if (ActiveTrimItem?.Id != item.Id)
        {
            return;
        }

        MediaDurationSeconds = durationSeconds;
        TrimEndSeconds = durationSeconds;
    }

    public bool HasEnoughTimelineFrames() =>
        TimelineFramePaths.Count == TimelineTargetFrameCount;

    public bool HasPartialTimelineFrames() =>
        TimelineFramePaths.Count > 0 && !HasEnoughTimelineFrames();

    public Task EnsureTimelineStripAsync(DownloadItem item)
    {
        if (!item.FileExists)
        {
            return Task.CompletedTask;
        }

        if (_timelineStripTasks.TryGetValue(item.Id, out var existing) && !existing.IsCompleted)
        {
            return existing;
        }

        _timelineStripTasks.TryRemove(item.Id, out _);
        var task = RunTimelineStripAsync(item);
        _timelineStripTasks[item.Id] = task;
        return task;
    }

    /// <summary>
    /// Sets duration/aspect/frame count so the timeline can show placeholder cells, then starts
    /// filmstrip generation in the background (progressive fill is fine).
    /// </summary>
    public async Task PrepareTimelineStripPlaceholdersAsync(DownloadItem item)
    {
        if (!item.FileExists || IsAudioMode)
        {
            return;
        }

        if (TryApplyCachedTimelineStrip(item))
        {
            return;
        }

        var duration = await GetOrProbeDurationAsync(item.FilePath);
        var dimensions = await GetOrProbeDimensionsAsync(item.FilePath);
        if (duration <= 0 || !dimensions.IsValid)
        {
            return;
        }

        var frameCount = FilmstripLayout.CalculateFrameCount(
            FilmstripLayout.DefaultFilmstripWidth,
            dimensions.AspectRatio);

        await UiDispatcher.RunAsync(() =>
        {
            if (ActiveTrimItem?.Id != item.Id)
            {
                return;
            }

            TimelineTargetFrameCount = frameCount;
            ApplyTimelineAspect(dimensions);
            MediaDurationSeconds = duration;
            TrimEndSeconds = duration;
            TimelineFramePaths = [];
            _lastPublishedTimelineFrameCount = 0;
        });
    }

    private CancellationTokenSource GetOrCreateTimelineStripToken(string itemId)
    {
        if (_timelineStripTokens.TryGetValue(itemId, out var existing)
            && !existing.IsCancellationRequested)
        {
            return existing;
        }

        CancelTimelineStripWork(itemId);
        var created = new CancellationTokenSource();
        _timelineStripTokens[itemId] = created;
        return created;
    }

    private void CancelTimelineStripWork(string itemId)
    {
        if (_timelineStripTokens.TryRemove(itemId, out var token))
        {
            token.Cancel();
            token.Dispose();
        }

        _timelineStripTasks.TryRemove(itemId, out _);
    }

    private async Task RunTimelineStripAsync(DownloadItem item)
    {
        var requestId = item.Id;
        var cancellationToken = GetOrCreateTimelineStripToken(item.Id).Token;

        try
        {
            await _timelineLoadGate.WaitAsync(cancellationToken);
            try
            {
                var durationTask = GetOrProbeDurationAsync(item.FilePath);
                var dimensionsTask = GetOrProbeDimensionsAsync(item.FilePath);
                await Task.WhenAll(durationTask, dimensionsTask);
                var duration = durationTask.Result;
                var dimensions = dimensionsTask.Result;
                if (duration <= 0 || cancellationToken.IsCancellationRequested)
                {
                    AppDiagnostic.Log($"Timeline strip skipped (duration={duration:0.###} cancelled={cancellationToken.IsCancellationRequested})");
                    return;
                }

                if (!ExternalToolLocator.IsFfmpegAvailable)
                {
                    AppDiagnostic.Log("Timeline strip skipped: ffmpeg not found");
                    return;
                }

                var frameCount = FilmstripLayout.CalculateFrameCount(
                    FilmstripLayout.DefaultFilmstripWidth,
                    dimensions.AspectRatio);

                await PublishTimelineStripMetadataAsync(requestId, dimensions, duration, frameCount);

                var cached = _thumbnailStrip.ReadCachedFrames(item.Id, duration, dimensions, frameCount);
                if (cached.Count == frameCount)
                {
                    await PublishTimelineStripAsync(requestId, cached, dimensions, duration, frameCount);
                    return;
                }

                var progress = new Progress<IReadOnlyList<string>>(frames =>
                {
                    _ = PublishTimelineStripProgressiveAsync(requestId, frames, dimensions, duration, frameCount);
                });

                var frames = await _thumbnailStrip.GenerateAsync(
                    item.FilePath,
                    item.Id,
                    duration,
                    dimensions,
                    frameCount,
                    progress,
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested || ActiveTrimItem?.Id != requestId)
                {
                    return;
                }

                await PublishTimelineStripAsync(requestId, frames, dimensions, duration, frameCount);
                AppDiagnostic.Log(
                    $"Timeline strip ready {frames.Count}/{frameCount} for {item.FileName} cache={_thumbnailStrip.ReadCachedFrames(item.Id, duration, dimensions, frameCount).Count}");
            }
            finally
            {
                _timelineLoadGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            AppDiagnostic.Log($"Timeline strip cancelled for {item.FileName}");
        }
        catch (Exception ex)
        {
            AppDiagnostic.Log($"Timeline strip failed: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Timeline strip failed: {ex.Message}");
        }
        finally
        {
            _timelineStripTasks.TryRemove(item.Id, out _);
        }
    }

    private Task PublishTimelineStripMetadataAsync(
        string requestId,
        VideoDimensions dimensions,
        double durationSeconds,
        int frameCount) =>
        UiDispatcher.RunAsync(() =>
        {
            if (ActiveTrimItem?.Id != requestId)
            {
                return;
            }

            TimelineTargetFrameCount = frameCount;
            ApplyTimelineAspect(dimensions);
            MediaDurationSeconds = durationSeconds;
            TrimEndSeconds = durationSeconds;
        });

    private Task PublishTimelineStripProgressiveAsync(
        string requestId,
        IReadOnlyList<string> frames,
        VideoDimensions dimensions,
        double durationSeconds,
        int frameCount)
    {
        var paths = frames
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Take(frameCount)
            .ToArray();

        if (paths.Length == 0)
        {
            return Task.CompletedTask;
        }

        return UiDispatcher.RunAsync(() =>
        {
            if (ActiveTrimItem?.Id != requestId)
            {
                return;
            }

            TimelineTargetFrameCount = frameCount;
            ApplyTimelineAspect(dimensions);
            MediaDurationSeconds = durationSeconds;
            TrimEndSeconds = durationSeconds;

            if (paths.Length < TimelineFramePaths.Count
                && paths.Length > 0
                && TimelineFramePaths.Count > 0
                && string.Equals(paths[0], TimelineFramePaths[0], StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _lastPublishedTimelineFrameCount = paths.Length;
            _lastTimelinePublishUtc = DateTimeOffset.UtcNow;
            TimelineFramePaths = paths;

            if (paths.Length >= frameCount)
            {
                PreloadFilmstripImages(paths);
            }
        });
    }

    private Task PublishTimelineStripAsync(
        string requestId,
        IReadOnlyList<string> frames,
        VideoDimensions dimensions,
        double durationSeconds,
        int frameCount) =>
        PublishTimelineStripProgressiveAsync(requestId, frames, dimensions, durationSeconds, frameCount);

    public bool TryApplyCachedTimelineStrip(DownloadItem item)
    {
        var fullPath = Path.GetFullPath(item.FilePath);
        if (!_probedDurations.TryGetValue(fullPath, out var duration) || duration <= 0)
        {
            return false;
        }

        if (!_probedDimensions.TryGetValue(fullPath, out var dimensions) || !dimensions.IsValid)
        {
            return false;
        }

        var frameCount = FilmstripLayout.CalculateFrameCount(
            FilmstripLayout.DefaultFilmstripWidth,
            dimensions.AspectRatio);

        var cached = _thumbnailStrip.ReadCachedFrames(item.Id, duration, dimensions, frameCount);
        if (cached.Count != frameCount)
        {
            return false;
        }

        TimelineTargetFrameCount = frameCount;
        ApplyTimelineAspect(dimensions);
        MediaDurationSeconds = duration;
        TrimEndSeconds = duration;
        TimelineFramePaths = cached.ToArray();
        _lastPublishedTimelineFrameCount = cached.Count;
        PreloadFilmstripImages(cached);
        return true;
    }

    private async Task<double> GetOrProbeDurationAsync(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (_probedDurations.TryGetValue(fullPath, out var cached) && cached > 0)
        {
            return cached;
        }

        var duration = await MediaProbeService.GetDurationSecondsAsync(fullPath);
        if (duration > 0)
        {
            _probedDurations[fullPath] = duration;
        }

        return duration;
    }

    private async Task<VideoDimensions> GetOrProbeDimensionsAsync(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (_probedDimensions.TryGetValue(fullPath, out var cached) && cached.IsValid)
        {
            return cached;
        }

        var dimensions = await MediaProbeService.GetVideoDimensionsAsync(fullPath);
        if (dimensions.IsValid)
        {
            _probedDimensions[fullPath] = dimensions;
            return dimensions;
        }

        return VideoDimensions.Default;
    }

    private void ApplyTimelineAspect(VideoDimensions dimensions)
    {
        TimelineFrameAspectRatio = dimensions.OrDefault().AspectRatio;
    }

    private void PreloadFilmstripImages(IReadOnlyList<string> paths)
    {
        var decodeWidth = Math.Max(
            1,
            (int)Math.Round(TimelineFrameAspectRatio * ThumbnailStripService.FrameHeight));
        var pathsCopy = paths.ToArray();

        _ = App.DispatcherQueue?.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                foreach (var path in pathsCopy)
                {
                    _ = ImageFileSource.FromPath(path, decodeWidth);
                }
            });
    }

    private async Task EnsureThumbnailAsync(DownloadItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ThumbnailPath)
            && File.Exists(item.ThumbnailPath)
            && item.ThumbnailPath.EndsWith(".ar.jpg", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await ThumbnailGenerationGate.WaitAsync();
        try
        {
            if (!string.IsNullOrWhiteSpace(item.ThumbnailPath)
                && File.Exists(item.ThumbnailPath)
                && item.ThumbnailPath.EndsWith(".ar.jpg", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var path = await _thumbnailGenerator.GeneratePosterAsync(item.FilePath);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await UiDispatcher.RunAsync(() => item.ThumbnailPath = path);

            ScheduleSaveHistory();
        }
        finally
        {
            ThumbnailGenerationGate.Release();
        }
    }

    private TrimSelection CurrentTrimSelection()
    {
        if (GetTrimSelectionFromView is not null)
        {
            var fromView = GetTrimSelectionFromView();
            if (fromView.DurationSeconds >= 0.25)
            {
                SyncTrimSelectionFromView(fromView);
                return fromView;
            }
        }

        var start = Math.Max(0, TrimStartSeconds);
        var end = Math.Max(start + 0.25, TrimEndSeconds);
        return new TrimSelection(start, end);
    }

    private void SyncTrimSelectionFromView(TrimSelection selection)
    {
        TrimStartSeconds = selection.StartSeconds;
        TrimEndSeconds = selection.EndSeconds;
    }

    private void ScheduleSaveHistory()
    {
        _historySaveDebounceCts?.Cancel();
        _historySaveDebounceCts?.Dispose();
        _historySaveDebounceCts = new CancellationTokenSource();
        var token = _historySaveDebounceCts.Token;
        _ = DebouncedSaveHistoryAsync(token);
    }

    private async Task DebouncedSaveHistoryAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(600, token);
            await SaveHistoryAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SaveHistoryAsync()
    {
        try
        {
            await _storage.SaveHistoryAsync(History);
        }
        catch (Exception ex)
        {
            AppDiagnostic.LogException("SaveHistory", ex);
        }
    }

    public async Task RefreshDependenciesAsync(bool force = false)
    {
        if (!force
            && _depsStatusValid
            && DependenciesReady
            && (DateTimeOffset.UtcNow - _depsStatusCheckedUtc).TotalMilliseconds < DependencyCheckTtlMs)
        {
            return;
        }

        if (force)
        {
            ExternalToolLocator.Invalidate();
        }

        var status = await _dependencySetup.CheckAsync();
        var wingetAvailable = await WingetLocator.IsAvailableAsync();
        await UiDispatcher.RunAsync(() =>
        {
            DependenciesReady = status.IsReady;
            HasYtDlp = status.HasYtDlp;
            HasFfmpeg = status.HasFfmpeg;
            IsFfmpegAvailable = status.HasFfmpeg;
            if (!IsInstallingDependencies)
            {
                SyncSetupItemStatesFromStatus(status, wingetAvailable);
            }

            OnPropertyChanged(nameof(SetupVerifyButtonText));
            OnPropertyChanged(nameof(SetupInstallButtonText));
            OnPropertyChanged(nameof(SetupMissingToolsSummary));
        });

        _depsStatusValid = true;
        _depsStatusCheckedUtc = DateTimeOffset.UtcNow;
    }

    private void ApplySetupProgress(DependencySetupProgressReport report)
    {
        UiDispatcher.Run(() =>
        {
            if (!string.IsNullOrWhiteSpace(report.Message))
            {
                SetupProgressMessage = report.Message;
            }

            if (report.ActiveToolId is "winget")
            {
                WingetSetupState = DependencySetupItemState.Installing;
            }
            else if (report.ActiveToolId is "yt-dlp")
            {
                YtDlpSetupState = DependencySetupItemState.Installing;
            }
            else if (report.ActiveToolId is "ffmpeg")
            {
                FfmpegSetupState = DependencySetupItemState.Installing;
            }

            if (report.WingetAvailable == true)
            {
                WingetSetupState = DependencySetupItemState.Installed;
            }

            if (report.YtDlpInstalled == true)
            {
                HasYtDlp = true;
                YtDlpSetupState = DependencySetupItemState.Installed;
            }

            if (report.FfmpegInstalled == true)
            {
                HasFfmpeg = true;
                IsFfmpegAvailable = true;
                FfmpegSetupState = DependencySetupItemState.Installed;
            }

            OnPropertyChanged(nameof(SetupMissingToolsSummary));
        });
    }

    private void SyncSetupItemStatesFromStatus(DependencyStatus status, bool? wingetAvailable = null)
    {
        YtDlpSetupState = status.HasYtDlp
            ? DependencySetupItemState.Installed
            : DependencySetupItemState.Missing;
        FfmpegSetupState = status.HasFfmpeg
            ? DependencySetupItemState.Installed
            : DependencySetupItemState.Missing;

        var wingetReady = wingetAvailable ?? WingetLocator.ResolveExecutablePath() is not null;
        WingetSetupState = wingetReady
            ? DependencySetupItemState.Installed
            : DependencySetupItemState.Missing;
    }

    private static string DescribeSetupItemState(DependencySetupItemState state, bool installed) =>
        state switch
        {
            DependencySetupItemState.Installing => "Installing...",
            DependencySetupItemState.Installed => "Installed",
            DependencySetupItemState.Missing when !installed => "Required — not installed",
            DependencySetupItemState.Missing => "Required — not installed",
            _ => string.Empty,
        };

    private void SetStatus(string message) =>
        UiDispatcher.Run(() => StatusMessage = message);

    private void ClearStatus() =>
        UiDispatcher.Run(() => StatusMessage = string.Empty);

    private async Task ClearCopiedIndicatorAsync(string id)
    {
        await Task.Delay(1000);
        if (CopiedHistoryItemId == id)
        {
            CopiedHistoryItemId = null;
        }

        if (History.FirstOrDefault(item => item.Id == id) is { } copiedItem)
        {
            copiedItem.IsCopied = false;
        }
    }

    private async Task ClearTrimCopyIndicatorAsync()
    {
        await Task.Delay(1500);
        TrimCopySucceeded = false;
    }

    private async Task ClearTrimGifCopyIndicatorAsync()
    {
        await Task.Delay(1500);
        TrimGifCopySucceeded = false;
    }

    private async Task ClearHistoryGifCopiedIndicatorAsync(string id)
    {
        await Task.Delay(1500);
        if (History.FirstOrDefault(item => item.Id == id) is { } copiedItem)
        {
            copiedItem.IsGifCopied = false;
        }
    }

    private async Task ClearTrimSaveIndicatorAsync()
    {
        await Task.Delay(1500);
        TrimSaveSucceeded = false;
    }

    private static bool LooksLikeWebUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsSubmittableUrl(string url)
    {
        if (!LooksLikeWebUrl(url) || !YtDlpSupportedHostIndex.IsSupportedUrl(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath.Trim('/').Length > 0;
        }

        var path = uri.AbsolutePath.Trim('/');
        if (path.Length == 0)
        {
            return false;
        }

        if (path.Equals("watch", StringComparison.OrdinalIgnoreCase)
            && !uri.Query.Contains("v=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private async Task<bool> TryCommitUrlAsync(string url, bool updateSourceField)
    {
        url = url.Trim();
        if (IsDownloadBusy || IsUpdateBusy || IsTrimExporting || ShowDependencySetup)
        {
            return false;
        }

        if (!IsSubmittableUrl(url))
        {
            return false;
        }

        var existing = FindDownloadedHistoryItemInternal(url);
        if (existing is not null)
        {
            await UiDispatcher.RunAsync(() => FocusExistingHistoryItemInternal(existing, url));
            return true;
        }

        if (updateSourceField)
        {
            await UiDispatcher.RunAsync(() =>
            {
                SourceUrl = url;
                ClearStatus();
            });
        }
        else if (!string.Equals(SourceUrl.Trim(), url, StringComparison.Ordinal))
        {
            return false;
        }

        await DownloadAsync();
        return true;
    }

    private DownloadItem? FindDownloadedHistoryItemInternal(string url)
    {
        var matchKey = MediaUrlNormalizer.GetMatchKey(url);
        return History.FirstOrDefault(item =>
            item.FileExists
            && string.Equals(MediaUrlNormalizer.GetMatchKey(item.SourceUrl), matchKey, StringComparison.Ordinal));
    }

    private void FocusExistingHistoryItemInternal(DownloadItem item, string url)
    {
        SelectedHistoryItem = item;
        ActiveTrimItem = null;
        SourceUrl = url;
        HistoryListScrollTarget = item;
        OnPropertyChanged(nameof(HistoryListScrollTarget));
        SetStatus(UserFacingMessages.AlreadyInLibrary);
    }

    private void OpenExistingHistoryItem(DownloadItem item)
    {
        SelectedHistoryItem = item;
        ActiveTrimItem = item;
        SourceUrl = string.Empty;
        HistoryListScrollTarget = item;
        OnPropertyChanged(nameof(HistoryListScrollTarget));
        ClearStatus();
    }

#if DEBUG
    public Task StartSmokeDownloadAsync(string url) => TryCommitUrlAsync(url.Trim(), updateSourceField: true);

    public DownloadItem RegisterSmokeTestVideo(string filePath)
    {
        filePath = Path.GetFullPath(filePath);
        var item = new DownloadItem
        {
            SourceUrl = "smoke://local",
            Title = Path.GetFileNameWithoutExtension(filePath),
            FilePath = filePath,
            CreatedAt = DateTimeOffset.Now,
        };

        History.Insert(0, item);
        _ = SaveHistoryAsync();
        return item;
    }

    public (double Aspect, int TargetFrames, int ReadyFrames, double Duration) GetTimelineStripStatus() =>
        (
            TimelineFrameAspectRatio,
            TimelineTargetFrameCount,
            TimelineFramePaths.Count,
            MediaDurationSeconds);
#endif
}
