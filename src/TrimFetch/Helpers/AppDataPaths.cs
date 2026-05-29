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
            string baseRoot;
            try
            {
                baseRoot = Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path,
                    AppBranding.StorageFolderName);
            }
            catch
            {
                baseRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppBranding.StorageFolderName);
            }

#if DEBUG
            // Dev/smoke runs (winapp-run, TRIMFETCH_SMOKE_*) must not pollute Release install data.
            return Path.Combine(baseRoot, "Dev");
#else
            return baseRoot;
#endif
        }
    }
}
