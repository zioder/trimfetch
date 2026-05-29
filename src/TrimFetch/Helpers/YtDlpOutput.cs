using System.Globalization;

namespace TrimFetch.Helpers;

public static class YtDlpOutput
{
    public static bool TryParseDownloadFraction(string line, out double fraction)
    {
        fraction = 0;
        line = StripConsoleEscapes(line);
        if (!line.Contains("[download]", StringComparison.Ordinal))
        {
            return false;
        }

        var percentIndex = line.IndexOf('%');
        if (percentIndex <= 0)
        {
            return false;
        }

        var start = percentIndex - 1;
        while (start > 0 && (char.IsDigit(line[start - 1]) || line[start - 1] == '.'))
        {
            start--;
        }

        if (!double.TryParse(
                line.AsSpan(start, percentIndex - start),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var percent))
        {
            return false;
        }

        fraction = Math.Clamp(percent, 0, 100) / 100d;
        return true;
    }

    /// <summary>Merge, extract, embed, and other yt-dlp steps after the last download percent.</summary>
    public static bool TryParsePostProcessFraction(string line, out double fraction)
    {
        fraction = 0;
        line = StripConsoleEscapes(line);
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (line.Contains("[download]", StringComparison.Ordinal))
        {
            return false;
        }

        if (TryParseTaggedPercent(line, "[Merger]", out fraction))
        {
            return true;
        }

        if (TryParseTaggedPercent(line, "[ffmpeg]", out fraction))
        {
            return true;
        }

        if (TryParseTaggedPercent(line, "[ExtractAudio]", out fraction))
        {
            return true;
        }

        if (line.Contains("[Merger]", StringComparison.Ordinal)
            || line.Contains("Merging formats", StringComparison.OrdinalIgnoreCase))
        {
            fraction = 0.35;
            return true;
        }

        if (line.Contains("[ExtractAudio]", StringComparison.Ordinal)
            || line.Contains("[ffmpeg]", StringComparison.Ordinal)
            || line.Contains("Post-process", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Embedding", StringComparison.OrdinalIgnoreCase))
        {
            fraction = 0.2;
            return true;
        }

        if (line.Contains("Deleting original", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Cleaning up", StringComparison.OrdinalIgnoreCase))
        {
            fraction = 0.92;
            return true;
        }

        return false;
    }

    /// <summary>Early yt-dlp work before byte progress (extract, format selection).</summary>
    public static bool TryParsePrefetchActivity(string line, out double fraction)
    {
        fraction = 0;
        line = StripConsoleEscapes(line);
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("[info]", StringComparison.Ordinal))
        {
            return false;
        }

        if (line.Contains("Downloading webpage", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Extracting URL", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Downloading tv", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Downloading x.com", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Downloading twitter", StringComparison.OrdinalIgnoreCase))
        {
            fraction = 0.06;
            return true;
        }

        if (line.Contains("Downloading metadata", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Downloading m3u8", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Downloading format", StringComparison.OrdinalIgnoreCase))
        {
            fraction = 0.12;
            return true;
        }

        return false;
    }

    public static bool IsPostProcessActivity(string line)
    {
        line = StripConsoleEscapes(line);
        return TryParsePostProcessFraction(line, out _)
            || line.Contains("[Merger]", StringComparison.Ordinal)
            || line.Contains("[ExtractAudio]", StringComparison.Ordinal)
            || line.Contains("[ffmpeg]", StringComparison.Ordinal);
    }

    private static bool TryParseTaggedPercent(string line, string tag, out double fraction)
    {
        fraction = 0;
        if (!line.Contains(tag, StringComparison.Ordinal))
        {
            return false;
        }

        var percentIndex = line.IndexOf('%');
        if (percentIndex <= 0)
        {
            return false;
        }

        var start = percentIndex - 1;
        while (start > 0 && (char.IsDigit(line[start - 1]) || line[start - 1] == '.'))
        {
            start--;
        }

        if (!double.TryParse(
                line.AsSpan(start, percentIndex - start),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var percent))
        {
            return false;
        }

        fraction = Math.Clamp(percent, 0, 100) / 100d;
        return true;
    }

    private static string StripConsoleEscapes(string line)
    {
        if (!line.Contains('\u001b'))
        {
            return line;
        }

        return System.Text.RegularExpressions.Regex.Replace(line, "\u001b\\[[0-9;]*[A-Za-z]", string.Empty);
    }

    public static string? ResolveDownloadedMediaPath(IEnumerable<string> outputLines, string folder, DateTimeOffset notBefore)
    {
        var video = ResolveDownloadedVideoPath(outputLines, folder, notBefore);
        if (video is not null)
        {
            return video;
        }

        return ResolveDownloadedAudioPath(outputLines, folder, notBefore);
    }

    public static string? ResolveDownloadedVideoPath(IEnumerable<string> outputLines, string folder, DateTimeOffset notBefore)
    {
        var candidates = outputLines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .Where(path => IsPlayableVideoFile(path) && File.Exists(path))
            .ToList();

        if (candidates.Count > 0)
        {
            return candidates[^1];
        }

        return FindNewestVideoFile(folder, notBefore);
    }

    public static string? ResolveDownloadedAudioPath(IEnumerable<string> outputLines, string folder, DateTimeOffset notBefore)
    {
        var candidates = outputLines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .Where(path => IsPlayableAudioFile(path) && File.Exists(path))
            .ToList();

        if (candidates.Count > 0)
        {
            return candidates[^1];
        }

        return FindNewestAudioFile(folder, notBefore);
    }

    public static bool IsPlayableVideoFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        if (name.Contains(".fhls-", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".ytdl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Path.GetExtension(path) is ".mp4" or ".m4v" or ".mov" or ".webm" or ".mkv";
    }

    public static bool IsPlayableAudioFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".ytdl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Path.GetExtension(path) is ".mp3" or ".m4a" or ".aac" or ".wav" or ".flac" or ".ogg" or ".opus" or ".wma";
    }

    private static string? FindNewestVideoFile(string folder, DateTimeOffset notBefore)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        return Directory.EnumerateFiles(folder)
            .Where(IsPlayableVideoFile)
            .Select(path => new FileInfo(path))
            .Where(info => info.LastWriteTimeUtc >= notBefore.UtcDateTime)
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => info.FullName)
            .FirstOrDefault();
    }

    private static string? FindNewestAudioFile(string folder, DateTimeOffset notBefore)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        return Directory.EnumerateFiles(folder)
            .Where(IsPlayableAudioFile)
            .Select(path => new FileInfo(path))
            .Where(info => info.LastWriteTimeUtc >= notBefore.UtcDateTime)
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => info.FullName)
            .FirstOrDefault();
    }
}
