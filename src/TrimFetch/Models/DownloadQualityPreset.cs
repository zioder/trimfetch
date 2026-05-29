namespace TrimFetch.Models;

public enum DownloadQualityPreset
{
    Best,
    Max1080p,
    Max720p,
    Smallest,
    AudioOnly,
}

public sealed record DownloadQualityOption(DownloadQualityPreset Preset, string DisplayName)
{
    public static IReadOnlyList<DownloadQualityOption> All { get; } =
    [
        new(DownloadQualityPreset.Best, "Best available (Recommended)"),
        new(DownloadQualityPreset.Max1080p, "Up to 1080p"),
        new(DownloadQualityPreset.Max720p, "Up to 720p"),
        new(DownloadQualityPreset.Smallest, "Smallest file size"),
        new(DownloadQualityPreset.AudioOnly, "Audio only"),
    ];
}
