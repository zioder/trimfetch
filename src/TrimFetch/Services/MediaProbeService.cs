using System.Diagnostics;
using System.Globalization;
using TrimFetch.Helpers;
using TrimFetch.Models;

namespace TrimFetch.Services;

public static class MediaProbeService
{
    public static async Task<(bool HasVideo, bool HasAudio)> GetStreamPresenceAsync(
        string mediaPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mediaPath))
        {
            return (false, false);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExternalToolLocator.FfprobeExecutable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        foreach (var argument in new[]
        {
            "-v", "error",
            "-show_entries", "stream=codec_type",
            "-of", "csv=p=0",
            mediaPath,
        })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                return (false, false);
            }

            var hasVideo = false;
            var hasAudio = false;
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var codecType = line.Trim();
                if (codecType.Equals("video", StringComparison.OrdinalIgnoreCase))
                {
                    hasVideo = true;
                }
                else if (codecType.Equals("audio", StringComparison.OrdinalIgnoreCase))
                {
                    hasAudio = true;
                }
            }

            return (hasVideo, hasAudio);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ffprobe streams failed for {mediaPath}: {ex.Message}");
            return (false, false);
        }
    }

    public static async Task<TrimMediaProfile> GetTrimMediaProfileAsync(
        string mediaPath,
        string? sourceUrl = null,
        CancellationToken cancellationToken = default)
    {
        var (hasVideo, hasAudio) = await GetStreamPresenceAsync(mediaPath, cancellationToken);
        if (hasVideo || hasAudio)
        {
            return MediaSourceAnalyzer.ProfileFromStreams(hasVideo, hasAudio);
        }

        if (MediaSourceAnalyzer.IsAudioFilePathOrUrl(mediaPath)
            || MediaSourceAnalyzer.IsLikelyAudioOnlySource(sourceUrl))
        {
            return TrimMediaProfile.AudioOnly;
        }

        return TrimMediaProfile.VideoWithAudio;
    }

    public static async Task<VideoDimensions> GetVideoDimensionsAsync(
        string videoPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(videoPath))
        {
            return default;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExternalToolLocator.FfprobeExecutable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        foreach (var argument in new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height",
            "-of", "csv=s=x:p=0",
            videoPath,
        })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                return default;
            }

            var text = output.Trim();
            var separator = text.IndexOf('x', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return default;
            }

            if (!int.TryParse(text[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                || !int.TryParse(text[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
                || width <= 0
                || height <= 0)
            {
                return default;
            }

            return new VideoDimensions(width, height);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ffprobe dimensions failed for {videoPath}: {ex.Message}");
            return default;
        }
    }

    public static async Task<double> GetDurationSecondsAsync(
        string videoPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(videoPath))
        {
            return 0;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExternalToolLocator.FfprobeExecutable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        foreach (var argument in new[]
        {
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1",
            videoPath,
        })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                return 0;
            }

            var text = output.Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && double.IsFinite(seconds)
                && seconds > 0
                ? seconds
                : 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ffprobe duration failed for {videoPath}: {ex.Message}");
            return 0;
        }
    }
}
