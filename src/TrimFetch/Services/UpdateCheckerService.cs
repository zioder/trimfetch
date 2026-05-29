using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrimFetch.Helpers;

namespace TrimFetch.Services;

public sealed class UpdateCheckerService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/zioder/trimfetch/releases/latest";

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppBranding.HttpUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var response = await client.GetAsync(LatestReleaseUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Could not read the latest GitHub release.");

        var latest = NormalizeVersion(release.TagName);
        var current = NormalizeVersion(currentVersion);

        if (CompareVersions(latest, current) <= 0)
        {
            return UpdateCheckResult.UpToDate;
        }

        var archSlug = GetInstallerArchSlug();
        var installers = release.Assets?
            .Where(IsTrimFetchSetupExe)
            .ToList() ?? [];

        var asset = installers.FirstOrDefault(a => MatchesArch(a.Name, archSlug))
            ?? installers.FirstOrDefault(a => MatchesArch(a.Name, "x64"))
            ?? installers.FirstOrDefault();

        return UpdateCheckResult.UpdateAvailable(
            latest,
            Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var releaseUri) ? releaseUri : null,
            asset?.BrowserDownloadUrl is { } download && Uri.TryCreate(download, UriKind.Absolute, out var downloadUri)
                ? downloadUri
                : null);
    }

    public async Task<DownloadedUpdate> DownloadAsync(
        UpdateCheckResult result,
        CancellationToken cancellationToken = default)
    {
        if (result.Kind != UpdateCheckResultKind.UpdateAvailable
            || string.IsNullOrWhiteSpace(result.Version)
            || result.DownloadUrl is null)
        {
            throw new InvalidOperationException("The latest GitHub release does not include a downloadable Windows installer.");
        }

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppBranding.HttpUserAgent);

        using var response = await client.GetAsync(result.DownloadUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        var directory = GetUpdateDownloadDirectory(result.Version);
        Directory.CreateDirectory(directory);

        var fileName = GetDownloadFileName(response, result.DownloadUrl);
        var destination = Path.Combine(directory, fileName);
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, cancellationToken);

        return new DownloadedUpdate(result.Version, destination);
    }

    private static string NormalizeVersion(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? trimmed[1..] : trimmed;
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var count = Math.Max(leftParts.Length, rightParts.Length);

        for (var index = 0; index < count; index++)
        {
            var leftValue = index < leftParts.Length && int.TryParse(leftParts[index], out var parsedLeft) ? parsedLeft : 0;
            var rightValue = index < rightParts.Length && int.TryParse(rightParts[index], out var parsedRight) ? parsedRight : 0;
            var compare = leftValue.CompareTo(rightValue);
            if (compare != 0)
            {
                return compare;
            }
        }

        return 0;
    }

    private static bool IsTrimFetchSetupExe(GitHubAsset asset) =>
        asset.Name.StartsWith("TrimFetchSetup-", StringComparison.OrdinalIgnoreCase)
        && asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesArch(string fileName, string archSlug) =>
        fileName.Contains($"-{archSlug}.exe", StringComparison.OrdinalIgnoreCase);

    private static string GetInstallerArchSlug() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x64",
            _ => "x64",
        };

    private static string GetUpdateDownloadDirectory(string version) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppBranding.StorageFolderName,
        "Updates",
        version);

    private static string GetDownloadFileName(HttpResponseMessage response, Uri fallbackUri)
    {
        var suggested = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;
        suggested = suggested?.Trim('"');

        if (!string.IsNullOrWhiteSpace(suggested))
        {
            return suggested;
        }

        var fallback = Path.GetFileName(fallbackUri.LocalPath);
        if (!string.IsNullOrWhiteSpace(fallback) && IsTrimFetchSetupExe(new GitHubAsset { Name = fallback }))
        {
            return fallback;
        }

        return AppBranding.DefaultUpdatePackageFileName;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}

public enum UpdateCheckResultKind
{
    UpToDate,
    UpdateAvailable,
}

public sealed record UpdateCheckResult(UpdateCheckResultKind Kind, string? Version = null, Uri? ReleaseUrl = null, Uri? DownloadUrl = null)
{
    public static UpdateCheckResult UpToDate { get; } = new(UpdateCheckResultKind.UpToDate);

    public static UpdateCheckResult UpdateAvailable(string version, Uri? releaseUrl, Uri? downloadUrl) =>
        new(UpdateCheckResultKind.UpdateAvailable, version, releaseUrl, downloadUrl);
}

public sealed record DownloadedUpdate(string Version, string FilePath);
