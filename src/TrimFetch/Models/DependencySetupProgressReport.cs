namespace TrimFetch.Models;

public sealed class DependencySetupProgressReport
{
    public string? Message { get; init; }

    /// <summary>Active step: winget, yt-dlp, or ffmpeg. Null when idle between steps.</summary>
    public string? ActiveToolId { get; init; }

    public bool? WingetAvailable { get; init; }

    public bool? YtDlpInstalled { get; init; }

    public bool? FfmpegInstalled { get; init; }
}
