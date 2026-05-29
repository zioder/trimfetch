namespace TrimFetch.Helpers;

/// <summary>
/// Resolves app storage at use time so packaged MSIX paths (LocalCache) are correct
/// for both in-process File I/O and external tools such as ffmpeg.
/// </summary>
internal static class AppDataPaths
{
    public static string StorageRoot
    {
        get
        {
            try
            {
                return Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path,
                    AppBranding.StorageFolderName);
            }
            catch
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppBranding.StorageFolderName);
            }
        }
    }
}
