using System.Text.RegularExpressions;

namespace TrimFetch.Helpers;

/// <summary>
/// Normalizes media page URLs so equivalent links (tracking params, mobile hosts) match history.
/// </summary>
public static partial class MediaUrlNormalizer
{
    private static readonly HashSet<string> TrackingQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "fbclid", "gclid", "si", "feature", "ref", "ref_src", "s", "t",
    };

    public static string GetMatchKey(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return url.Trim().ToLowerInvariant();
        }

        var host = NormalizeHost(uri.Host);
        if (TryGetYouTubeMatchKey(uri, host, out var youtubeKey))
        {
            return youtubeKey;
        }

        if (TryGetTikTokMatchKey(uri, host, out var tiktokKey))
        {
            return tiktokKey;
        }

        if (TryGetInstagramMatchKey(uri, host, out var instagramKey))
        {
            return instagramKey;
        }

        if (TryGetTwitterMatchKey(uri, host, out var twitterKey))
        {
            return twitterKey;
        }

        return BuildCanonicalUrl(uri, host);
    }

    public static bool AreSameSource(string left, string right) =>
        string.Equals(GetMatchKey(left), GetMatchKey(right), StringComparison.Ordinal);

    private static bool TryGetYouTubeMatchKey(Uri uri, string host, out string key)
    {
        key = string.Empty;
        if (!IsYouTubeHost(host))
        {
            return false;
        }

        var id = TryGetYouTubeVideoId(uri);
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        key = $"youtube:{id}";
        return true;
    }

    private static bool TryGetTikTokMatchKey(Uri uri, string host, out string key)
    {
        key = string.Empty;
        if (!host.EndsWith("tiktok.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = TikTokVideoIdRegex().Match(uri.AbsolutePath);
        if (!match.Success)
        {
            return false;
        }

        key = $"tiktok:{match.Groups[1].Value}";
        return true;
    }

    private static bool TryGetTwitterMatchKey(Uri uri, string host, out string key)
    {
        key = string.Empty;
        if (!IsTwitterHost(host))
        {
            return false;
        }

        var match = TwitterStatusIdRegex().Match(uri.AbsolutePath);
        if (!match.Success)
        {
            return false;
        }

        key = $"twitter:{match.Groups[1].Value}";
        return true;
    }

    private static bool IsTwitterHost(string host) =>
        host.Equals("x.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".x.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".twitter.com", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetInstagramMatchKey(Uri uri, string host, out string key)
    {
        key = string.Empty;
        if (!host.EndsWith("instagram.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = InstagramPostRegex().Match(uri.AbsolutePath);
        if (!match.Success)
        {
            return false;
        }

        key = $"instagram:{match.Groups[1].Value}/{match.Groups[2].Value}";
        return true;
    }

    private static string? TryGetYouTubeVideoId(Uri uri)
    {
        if (uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var id = uri.AbsolutePath.Trim('/').Split('/')[0];
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }

        var query = ParseQuery(uri.Query);
        if (query.TryGetValue("v", out var fromQuery) && !string.IsNullOrWhiteSpace(fromQuery))
        {
            return fromQuery;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2
            && (segments[0] is "shorts" or "live" or "embed" or "v")
            && !string.IsNullOrWhiteSpace(segments[1]))
        {
            return segments[1];
        }

        return null;
    }

    private static bool IsYouTubeHost(string host) =>
        host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase);

    private static string BuildCanonicalUrl(Uri uri, string host)
    {
        var builder = new UriBuilder(uri)
        {
            Host = host,
            Fragment = string.Empty,
            Port = uri.IsDefaultPort ? -1 : uri.Port,
        };

        var parsedQuery = ParseQuery(uri.Query);
        if (parsedQuery.Count > 0)
        {
            var keys = parsedQuery.Keys
                .Where(key => !TrackingQueryKeys.Contains(key))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            builder.Query = keys.Count == 0
                ? string.Empty
                : string.Join("&", keys.Select(key => $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(parsedQuery[key])}"));
        }

        var path = builder.Path.TrimEnd('/');
        if (path.Length == 0)
        {
            path = "/";
        }

        builder.Path = path;
        return builder.Uri.ToString().ToLowerInvariant();
    }

    private static string NormalizeHost(string host)
    {
        host = host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        return host;
    }

    [GeneratedRegex(@"/(?:[^/]+/)?status(?:es)?/(\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TwitterStatusIdRegex();

    [GeneratedRegex(@"/video/(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TikTokVideoIdRegex();

    [GeneratedRegex(@"/(p|reel|tv)/([^/?#]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InstagramPostRegex();

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        var trimmed = query.TrimStart('?');
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator < 0)
            {
                result[Uri.UnescapeDataString(part)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(part[..separator]);
            var value = Uri.UnescapeDataString(part[(separator + 1)..]);
            result[key] = value;
        }

        return result;
    }
}
