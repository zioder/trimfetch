using TrimFetch.Helpers;
using Xunit;

namespace TrimFetch.Tests;

public sealed class YtDlpOutputTests
{
    [Theory]
    [InlineData("[download]  45.2% of    3.33MiB at  1.23MiB/s ETA 00:02", 0.452)]
    [InlineData("[download] 100% of  464.83KiB", 1.0)]
    [InlineData("[download]   0.0% of    3.33MiB at  664.18KiB/s ETA 00:05", 0.0)]
    public void TryParseDownloadFraction_parses_yt_dlp_stdout_lines(string line, double expected)
    {
        Assert.True(YtDlpOutput.TryParseDownloadFraction(line, out var fraction));
        Assert.Equal(expected, fraction, precision: 3);
    }

    [Fact]
    public void TryParseDownloadFraction_rejects_non_progress_lines()
    {
        Assert.False(YtDlpOutput.TryParseDownloadFraction("[info] Downloading webpage", out _));
        Assert.False(YtDlpOutput.TryParseDownloadFraction("[download] Destination: C:\\out.mp4", out _));
    }

    [Fact]
    public void TryParsePrefetchActivity_parses_early_info_lines()
    {
        Assert.True(YtDlpOutput.TryParsePrefetchActivity("[info] Downloading webpage", out var early));
        Assert.True(early > 0);

        Assert.True(YtDlpOutput.TryParsePrefetchActivity("[info] Downloading m3u8 information", out var mid));
        Assert.True(mid > early);
    }

    [Fact]
    public void TrimPrepCreep_approaches_pre_reveal_max_without_hitting_one()
    {
        Assert.True(DownloadPipelineProgress.TrimPrepCreep(0) > 0.5);
        Assert.True(DownloadPipelineProgress.TrimPrepCreep(1) <= DownloadPipelineProgress.PreRevealMax);
        Assert.True(DownloadPipelineProgress.TrimPrepCreep(1) < 1);
        Assert.True(DownloadPipelineProgress.TrimPrepCreep(1) >= DownloadPipelineProgress.TrimPrepCreep(0.5));
    }

    [Fact]
    public void PostProcess_phase_finishes_above_download_and_below_full_bar()
    {
        var downloadDone = DownloadPipelineProgress.FromDownloadHighWater(1);
        var mergeDone = DownloadPipelineProgress.FromPostProcessPhase(1);
        Assert.True(mergeDone > downloadDone);
        Assert.True(mergeDone < DownloadPipelineProgress.PreRevealMax);
    }

    [Theory]
    [InlineData("[Merger] Merging formats into \"out.mp4\"", 0.35)]
    [InlineData("[ffmpeg] 100% of 120MiB", 1.0)]
    public void TryParsePostProcessFraction_parses_merge_and_ffmpeg_lines(string line, double expectedMin)
    {
        Assert.True(YtDlpOutput.TryParsePostProcessFraction(line, out var fraction));
        Assert.True(fraction >= expectedMin - 0.01);
    }

}
