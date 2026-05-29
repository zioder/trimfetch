using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrimFetch.Models;
using TrimFetch.Services;
using Windows.Storage;
using Windows.System;

namespace TrimFetch.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly DependencySetupService _dependencySetup = new();
    private readonly PreferencesService _preferences = new();
    private readonly StartupRegistrationService _startupRegistration = new();
    private readonly FilePickerService _filePicker = new();
    private bool _isLoadingPreferences;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallYtDlpCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallFfmpegCommand))]
    public partial bool HasYtDlp { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallYtDlpCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallFfmpegCommand))]
    public partial bool HasFfmpeg { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallYtDlpCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallFfmpegCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshToolsCommand))]
    public partial bool IsInstallingYtDlp { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallYtDlpCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallFfmpegCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshToolsCommand))]
    public partial bool IsInstallingFfmpeg { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshToolsCommand))]
    public partial bool IsRefreshingTools { get; set; }

    [ObservableProperty]
    public partial string ToolsStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenDownloadFolder))]
    [NotifyCanExecuteChangedFor(nameof(OpenDownloadFolderCommand))]
    public partial string DownloadFolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DownloadQualityOption SelectedDownloadQualityOption { get; set; } = DownloadQualityOption.All[0];

    [ObservableProperty]
    public partial bool AutoDownloadClipboardUrls { get; set; } = true;

    [ObservableProperty]
    public partial bool RunAtStartup { get; set; }

    [ObservableProperty]
    public partial bool ShowDownloadNotifications { get; set; } = true;

    public IReadOnlyList<DownloadQualityOption> DownloadQualityOptions { get; } = DownloadQualityOption.All;

    public event EventHandler? DependenciesChanged;

    public event EventHandler? PreferencesChanged;

    public string YtDlpStatusText => HasYtDlp ? "Installed" : "Not installed";

    public string FfmpegStatusText => HasFfmpeg ? "Installed" : "Not installed";

    public bool CanOpenDownloadFolder => !string.IsNullOrWhiteSpace(DownloadFolderPath);

    public async Task InitializeAsync()
    {
        await ReloadPreferencesAsync();
        await RefreshToolsAsync();
    }

    public Task ReloadPreferencesAsync() => LoadPreferencesAsync();

    [RelayCommand]
    private async Task ChooseDownloadFolderAsync()
    {
        var folder = await _filePicker.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        await SetDownloadFolderAsync(folder);
    }

    [RelayCommand(CanExecute = nameof(CanOpenDownloadFolder))]
    private async Task OpenDownloadFolderAsync()
    {
        try
        {
            Directory.CreateDirectory(DownloadFolderPath);
            var folder = await StorageFolder.GetFolderFromPathAsync(DownloadFolderPath);
            _ = await Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception ex)
        {
            ToolsStatusMessage = ex.Message;
        }
    }

    partial void OnSelectedDownloadQualityOptionChanged(DownloadQualityOption value)
    {
        if (_isLoadingPreferences)
        {
            return;
        }

        _ = PersistDownloadQualityAsync(value.Preset);
    }

    partial void OnAutoDownloadClipboardUrlsChanged(bool value)
    {
        if (_isLoadingPreferences)
        {
            return;
        }

        _ = PersistAutoDownloadClipboardUrlsAsync(value);
    }

    partial void OnRunAtStartupChanged(bool value)
    {
        if (_isLoadingPreferences)
        {
            return;
        }

        _ = PersistRunAtStartupAsync(value);
    }

    partial void OnShowDownloadNotificationsChanged(bool value)
    {
        if (_isLoadingPreferences)
        {
            return;
        }

        _ = PersistShowDownloadNotificationsAsync(value);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshTools))]
    private async Task RefreshToolsAsync()
    {
        IsRefreshingTools = true;
        try
        {
            await ApplyStatusAsync(await _dependencySetup.CheckAsync());
            ToolsStatusMessage = string.Empty;
        }
        finally
        {
            IsRefreshingTools = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstallYtDlp))]
    private async Task InstallYtDlpAsync() => await InstallToolCoreAsync("yt-dlp", installing => IsInstallingYtDlp = installing);

    [RelayCommand(CanExecute = nameof(CanInstallFfmpeg))]
    private async Task InstallFfmpegAsync() => await InstallToolCoreAsync("ffmpeg", installing => IsInstallingFfmpeg = installing);

    private async Task LoadPreferencesAsync()
    {
        _isLoadingPreferences = true;
        try
        {
            DownloadFolderPath = await _preferences.LoadDownloadFolderAsync();
            var quality = await _preferences.GetDownloadQualityAsync();
            SelectedDownloadQualityOption = DownloadQualityOptions.First(o => o.Preset == quality);
            AutoDownloadClipboardUrls = await _preferences.GetAutoDownloadClipboardUrlsAsync();
            RunAtStartup = await _preferences.GetRunAtStartupAsync();
            ShowDownloadNotifications = await _preferences.GetShowDownloadNotificationsAsync();
            Directory.CreateDirectory(DownloadFolderPath);
        }
        finally
        {
            _isLoadingPreferences = false;
        }
    }

    private async Task SetDownloadFolderAsync(string folder)
    {
        DownloadFolderPath = folder;
        Directory.CreateDirectory(DownloadFolderPath);
        await _preferences.SaveDownloadFolderAsync(folder);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task PersistDownloadQualityAsync(DownloadQualityPreset quality)
    {
        await _preferences.SetDownloadQualityAsync(quality);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task PersistAutoDownloadClipboardUrlsAsync(bool enabled)
    {
        await _preferences.SetAutoDownloadClipboardUrlsAsync(enabled);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task PersistRunAtStartupAsync(bool enabled)
    {
        await _startupRegistration.ApplyPreferenceAsync(enabled, _preferences);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task PersistShowDownloadNotificationsAsync(bool enabled)
    {
        await _preferences.SetShowDownloadNotificationsAsync(enabled);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task InstallToolCoreAsync(string tool, Action<bool> setInstalling)
    {
        setInstalling(true);
        ToolsStatusMessage = string.Empty;
        try
        {
            var progress = new Progress<DependencySetupProgressReport>(report =>
            {
                if (!string.IsNullOrWhiteSpace(report.Message))
                {
                    ToolsStatusMessage = report.Message;
                }
            });
            await _dependencySetup.InstallToolAsync(tool, progress);
            await ApplyStatusAsync(await _dependencySetup.CheckAsync());
            DependenciesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ToolsStatusMessage = ex.Message;
            await ApplyStatusAsync(await _dependencySetup.CheckAsync());
        }
        finally
        {
            setInstalling(false);
        }
    }

    private async Task ApplyStatusAsync(DependencyStatus status)
    {
        HasYtDlp = status.HasYtDlp;
        HasFfmpeg = status.HasFfmpeg;
        OnPropertyChanged(nameof(YtDlpStatusText));
        OnPropertyChanged(nameof(FfmpegStatusText));
        await Task.CompletedTask;
    }

    private bool CanRefreshTools() => !IsRefreshingTools && !IsInstallingYtDlp && !IsInstallingFfmpeg;

    private bool CanInstallYtDlp() => !HasYtDlp && !IsInstallingYtDlp && !IsInstallingFfmpeg;

    private bool CanInstallFfmpeg() => !HasFfmpeg && !IsInstallingYtDlp && !IsInstallingFfmpeg;
}
