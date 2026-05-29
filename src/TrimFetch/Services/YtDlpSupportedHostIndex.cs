using System.Reflection;

namespace TrimFetch.Services;

/// <summary>
/// Fast host lookup aligned with yt-dlp extractors (generated from extractor URL patterns).
/// Regenerate Assets/yt-dlp-supported-hosts.txt when updating yt-dlp.
/// </summary>
public static class YtDlpSupportedHostIndex
{
    private static readonly HashSet<string> AdditionalHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "x.com",
        "twitter.com",
        "mobile.twitter.com",
        "m.twitter.com",
    };

    private static readonly Lazy<HashSet<string>> Hosts = new(LoadHosts);

    public static bool IsSupportedUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return IsSupportedHost(uri.Host);
    }

    public static bool IsSupportedHost(string host)
    {
        host = NormalizeHost(host);
        if (IsBlockedHost(host))
        {
            return false;
        }

        var labels = host.Split('.');
        for (var i = 0; i < labels.Length; i++)
        {
            var suffix = string.Join('.', labels.AsSpan(i));
            if (Hosts.Value.Contains(suffix) || AdditionalHosts.Contains(suffix))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> LoadHosts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("yt-dlp-supported-hosts.txt", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Supported host list is missing.");

        using var reader = new StreamReader(stream);
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim().ToLowerInvariant();
            if (line.Length > 0)
            {
                hosts.Add(line);
            }
        }

        return hosts;
    }

    private static string NormalizeHost(string host)
    {
        host = host.Trim().ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        return host;
    }

    private static bool IsBlockedHost(string host) =>
        host is "localhost"
        or "127.0.0.1"
        or "0.0.0.0"
        or "[::1]"
        || host.EndsWith(".local", StringComparison.Ordinal)
        || host.EndsWith(".internal", StringComparison.Ordinal);
}
