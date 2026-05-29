using TrimFetch.Models;

namespace TrimFetch.Helpers;

/// <summary>URL/path heuristics before and after download; file probe is authoritative when available.</summary>
public static class MediaSourceAnalyzer
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".oga",
    };

    private static readonly string[] AudioFirstHosts =
    [
        "open.spotify.com",
        "spotify.com",
        "music.youtube.com",
        "soundcloud.com",
        "music.apple.com",
        "bandcamp.com",
        "deezer.com",
        "tidal.com",
        "audiomack.com",
    ];

    public static bool IsLikelyAudioOnlySource(string? urlOrPath)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath))
        {
            return false;
        }

        if (IsAudioFilePathOrUrl(urlOrPath))
        {
            return true;
        }

        if (!Uri.TryCreate(urlOrPath.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        foreach (var audioHost in AudioFirstHosts)
        {
            if (host.Equals(audioHost, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + audioHost, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsAudioFilePathOrUrl(string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
        {
            return false;
        }

        string path;
        if (Uri.TryCreate(pathOrUrl.Trim(), UriKind.Absolute, out var uri))
        {
            path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
        }
        else
        {
            path = pathOrUrl.Trim();
        }

        var extension = Path.GetExtension(path);
        return !string.IsNullOrEmpty(extension) && AudioExtensions.Contains(extension);
    }

    public static TrimMediaProfile SuggestProfileFromUrl(string? urlOrPath) =>
        IsLikelyAudioOnlySource(urlOrPath)
            ? TrimMediaProfile.AudioOnly
            : TrimMediaProfile.VideoWithAudio;

    public static TrimMediaProfile ProfileFromStreams(bool hasVideo, bool hasAudio)
    {
        if (hasVideo && hasAudio)
        {
            return TrimMediaProfile.VideoWithAudio;
        }

        if (hasVideo)
        {
            return TrimMediaProfile.VideoOnly;
        }

        if (hasAudio)
        {
            return TrimMediaProfile.AudioOnly;
        }

        return TrimMediaProfile.VideoWithAudio;
    }

    public static DownloadQualityPreset ResolveDownloadQuality(string? urlOrPath, DownloadQualityPreset userPreset) =>
        IsLikelyAudioOnlySource(urlOrPath) ? DownloadQualityPreset.AudioOnly : userPreset;
}
