using System.Text.Json;
using Windows.System;
using TrimFetch.Helpers;
using TrimFetch.Models;

namespace TrimFetch.Services;

public sealed class PreferencesService
{
    public event EventHandler<HotKeyAction>? HotKeyChanged;

    private static PreferencesDocument? _sharedCache;
    private static DateTime _sharedCacheFileUtc;

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string AppDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppBranding.StorageFolderName);

    public string DefaultDownloadFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads");

    private string PreferencesPath => Path.Combine(AppDataFolder, "preferences.json");

    public async Task<string> LoadDownloadFolderAsync()
    {
        var preferences = await LoadAsync();
        return string.IsNullOrWhiteSpace(preferences.DownloadFolder)
            ? DefaultDownloadFolder
            : preferences.DownloadFolder;
    }

    public async Task SaveDownloadFolderAsync(string folder)
    {
        var preferences = await LoadAsync();
        preferences.DownloadFolder = folder;
        await SaveAsync(preferences);
    }

    public async Task<DownloadQualityPreset> GetDownloadQualityAsync()
    {
        var preferences = await LoadAsync();
        return preferences.DownloadQuality;
    }

    public async Task SetDownloadQualityAsync(DownloadQualityPreset quality)
    {
        var preferences = await LoadAsync();
        preferences.DownloadQuality = quality;
        await SaveAsync(preferences);
    }

    public async Task<bool> GetAutoDownloadClipboardUrlsAsync()
    {
        var preferences = await LoadAsync();
        return preferences.AutoDownloadClipboardUrls;
    }

    public async Task SetAutoDownloadClipboardUrlsAsync(bool enabled)
    {
        var preferences = await LoadAsync();
        preferences.AutoDownloadClipboardUrls = enabled;
        await SaveAsync(preferences);
    }

    public async Task<bool> GetRunAtStartupAsync()
    {
        var preferences = await LoadAsync();
        return preferences.RunAtStartup;
    }

    public async Task SetRunAtStartupAsync(bool enabled)
    {
        var preferences = await LoadAsync();
        preferences.RunAtStartup = enabled;
        await SaveAsync(preferences);
    }

    public async Task<bool> GetShowDownloadNotificationsAsync()
    {
        var preferences = await LoadAsync();
        return preferences.ShowDownloadNotifications;
    }

    public async Task SetShowDownloadNotificationsAsync(bool enabled)
    {
        var preferences = await LoadAsync();
        preferences.ShowDownloadNotifications = enabled;
        await SaveAsync(preferences);
    }

    public async Task<HotKeyShortcut> GetHotKeyAsync(HotKeyAction action)
    {
        var preferences = await LoadAsync();
        if (preferences.HotKeys.TryGetValue(action.ToString(), out var stored))
        {
            return stored.ToShortcut();
        }

        return action.GetDefaultShortcut();
    }

    public async Task SetHotKeyAsync(HotKeyAction action, HotKeyShortcut shortcut)
    {
        var preferences = await LoadAsync();
        preferences.HotKeys[action.ToString()] = HotKeyShortcutData.FromShortcut(shortcut);
        await SaveAsync(preferences);
        HotKeyChanged?.Invoke(this, action);
    }

    private async Task<PreferencesDocument> LoadAsync()
    {
        Directory.CreateDirectory(AppDataFolder);

        if (!File.Exists(PreferencesPath))
        {
            return new PreferencesDocument { DownloadFolder = DefaultDownloadFolder };
        }

        var fileUtc = File.GetLastWriteTimeUtc(PreferencesPath);
        if (_sharedCache is not null && fileUtc == _sharedCacheFileUtc)
        {
            return _sharedCache;
        }

        try
        {
            await using var stream = File.OpenRead(PreferencesPath);
            var document = await JsonSerializer.DeserializeAsync<PreferencesDocument>(stream, _jsonOptions)
                ?? new PreferencesDocument { DownloadFolder = DefaultDownloadFolder };
            _sharedCache = document;
            _sharedCacheFileUtc = fileUtc;
            return document;
        }
        catch
        {
            return new PreferencesDocument { DownloadFolder = DefaultDownloadFolder };
        }
    }

    private async Task SaveAsync(PreferencesDocument document)
    {
        Directory.CreateDirectory(AppDataFolder);
        await using var stream = File.Create(PreferencesPath);
        await JsonSerializer.SerializeAsync(stream, document, _jsonOptions);
        _sharedCache = document;
        _sharedCacheFileUtc = File.Exists(PreferencesPath)
            ? File.GetLastWriteTimeUtc(PreferencesPath)
            : DateTime.UtcNow;
    }

    private sealed class PreferencesDocument
    {
        public string DownloadFolder { get; set; } = string.Empty;

        public DownloadQualityPreset DownloadQuality { get; set; } = DownloadQualityPreset.Best;

        public bool AutoDownloadClipboardUrls { get; set; } = true;

        public bool RunAtStartup { get; set; }

        public bool ShowDownloadNotifications { get; set; } = true;

        public Dictionary<string, HotKeyShortcutData> HotKeys { get; set; } = new();
    }

    private sealed record HotKeyShortcutData(int Key, HotKeyModifiers Modifiers)
    {
        public static HotKeyShortcutData FromShortcut(HotKeyShortcut shortcut) =>
            new((int)shortcut.Key, shortcut.Modifiers);

        public HotKeyShortcut ToShortcut() => new((VirtualKey)Key, Modifiers);
    }
}
