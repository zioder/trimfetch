using TrimFetch.Models;

namespace TrimFetch.Helpers;

/// <summary>
/// Keeps download history free of debug smoke entries and broken dev paths.
/// </summary>
internal static class HistoryPersistence
{
    public static List<DownloadItem> FilterPersistable(IEnumerable<DownloadItem> items) =>
        items.Where(ShouldPersist).ToList();

    public static bool ShouldPersist(DownloadItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FilePath))
        {
            return false;
        }

        if (item.SourceUrl.StartsWith("smoke://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = item.FileName;
        var title = item.Title ?? string.Empty;
        if (LooksLikeDevSmokeArtifact(fileName) || LooksLikeDevSmokeArtifact(title))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(item.FilePath);
        }
        catch
        {
            return false;
        }

        if (IsDevelopmentArtifactPath(fullPath))
        {
            return false;
        }

        return File.Exists(fullPath);
    }

    private static bool LooksLikeDevSmokeArtifact(string value) =>
        value.Contains("winvid-smoke", StringComparison.OrdinalIgnoreCase)
        || value.Contains("winapp-run", StringComparison.OrdinalIgnoreCase)
        || value.Contains("winapp_smoke", StringComparison.OrdinalIgnoreCase);

    private static bool IsDevelopmentArtifactPath(string fullPath)
    {
        if (string.Equals(Path.GetFileName(fullPath), "preview.mp4", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalized = fullPath.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/test-assets/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/TrimFetch.StripBenchmark/", StringComparison.OrdinalIgnoreCase);
    }
}
