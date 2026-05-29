using Microsoft.UI.Xaml.Media.Imaging;

namespace TrimFetch.Helpers;

internal static class ImageFileSource
{
    private const int MaxCacheEntries = 192;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> LruOrder = new();

    public static bool IsDecoded(BitmapImage? bitmap) =>
        bitmap is not null && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0;

    public static BitmapImage? FromPath(string? path, int decodePixelWidth = 240)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        var key = $"{decodePixelWidth}|{fullPath}";
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                TouchLru(key);
                return cached;
            }
        }

        try
        {
            var bitmap = CreateBitmapFromFile(fullPath, decodePixelWidth);
            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out var existing))
                {
                    TouchLru(key);
                    return existing;
                }

                EvictIfNeeded();
                Cache[key] = bitmap;
                LruOrder.AddLast(key);
            }

            return bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ImageFileSource failed for {fullPath}: {ex.Message}");
            return null;
        }
    }

    public static void ClearCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
            LruOrder.Clear();
        }
    }

    private static void TouchLru(string key)
    {
        LruOrder.Remove(key);
        LruOrder.AddLast(key);
    }

    private static void EvictIfNeeded()
    {
        while (Cache.Count >= MaxCacheEntries && LruOrder.First is not null)
        {
            var oldest = LruOrder.First.Value;
            LruOrder.RemoveFirst();
            Cache.Remove(oldest);
        }
    }

    private static BitmapImage CreateBitmapFromFile(string fullPath, int decodePixelWidth)
    {
        var bitmap = new BitmapImage
        {
            UriSource = new Uri(fullPath, UriKind.Absolute),
        };

        if (decodePixelWidth > 0)
        {
            bitmap.DecodePixelWidth = decodePixelWidth;
        }

        return bitmap;
    }

    private static string NormalizePath(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        return Path.GetFullPath(path);
    }
}
