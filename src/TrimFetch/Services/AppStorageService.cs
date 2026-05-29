using System.Text.Json;
using TrimFetch.Helpers;
using TrimFetch.Models;

namespace TrimFetch.Services;

public sealed class AppStorageService
{
    private static readonly SemaphoreSlim HistorySaveGate = new(1, 1);

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string AppDataFolder => AppDataPaths.StorageRoot;

    public string DefaultDownloadFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads");

    private string PreferencesPath => Path.Combine(AppDataFolder, "preferences.json");

    private string HistoryPath => Path.Combine(AppDataFolder, "history.json");

    public async Task<string> LoadDownloadFolderAsync()
    {
        Directory.CreateDirectory(AppDataFolder);

        if (!File.Exists(PreferencesPath))
        {
            return DefaultDownloadFolder;
        }

        try
        {
            await using var stream = File.OpenRead(PreferencesPath);
            var preferences = await JsonSerializer.DeserializeAsync<AppPreferences>(stream, _jsonOptions);
            return string.IsNullOrWhiteSpace(preferences?.DownloadFolder)
                ? DefaultDownloadFolder
                : preferences.DownloadFolder;
        }
        catch
        {
            return DefaultDownloadFolder;
        }
    }

    public async Task SaveDownloadFolderAsync(string folder)
    {
        Directory.CreateDirectory(AppDataFolder);
        await using var stream = File.Create(PreferencesPath);
        await JsonSerializer.SerializeAsync(stream, new AppPreferences(folder), _jsonOptions);
    }

    public async Task<IReadOnlyList<DownloadItem>> LoadHistoryAsync()
    {
        Directory.CreateDirectory(AppDataFolder);

        if (!File.Exists(HistoryPath))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(
                HistoryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var loaded = await JsonSerializer.DeserializeAsync<List<DownloadItem>>(stream, _jsonOptions) ?? [];
            var valid = HistoryPersistence.FilterPersistable(loaded);
            if (valid.Count != loaded.Count)
            {
                await SaveHistoryAsync(valid);
            }

            return valid;
        }
        catch
        {
            return [];
        }
    }

    public async Task SaveHistoryAsync(IEnumerable<DownloadItem> items)
    {
        await HistorySaveGate.WaitAsync();
        try
        {
            items = HistoryPersistence.FilterPersistable(items);
            Directory.CreateDirectory(AppDataFolder);
            var tempPath = HistoryPath + ".tmp";
            await using (var stream = new FileStream(
                               tempPath,
                               FileMode.Create,
                               FileAccess.Write,
                               FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, items, _jsonOptions);
            }

            await ReplaceFileWithRetryAsync(tempPath, HistoryPath);
        }
        finally
        {
            HistorySaveGate.Release();
        }
    }

    private static async Task ReplaceFileWithRetryAsync(string sourcePath, string destinationPath)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (File.Exists(destinationPath))
                {
                    File.Replace(sourcePath, destinationPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(sourcePath, destinationPath);
                }

                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                await Task.Delay(40 * (attempt + 1));
            }
        }
    }

    private sealed record AppPreferences(string DownloadFolder);
}
