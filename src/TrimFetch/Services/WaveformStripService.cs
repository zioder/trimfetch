using System.Diagnostics;
using System.Globalization;
using TrimFetch.Helpers;

namespace TrimFetch.Services;

/// <summary>
/// Coarse waveform peaks for the trim timeline (one ffmpeg decode pass, disk-cached).
/// </summary>
public sealed class WaveformStripService
{
    private const int SampleRateHz = 200;
    private const int MinPeakBytes = 8;

    private static string RootFolder =>
        Path.Combine(AppDataPaths.StorageRoot, "WaveformPeaks");

    public float[]? ReadCachedPeaks(string itemId, double durationSeconds, int columnCount)
    {
        if (durationSeconds <= 0 || columnCount <= 0)
        {
            return null;
        }

        var path = GetCachePath(itemId, durationSeconds, columnCount);
        return ReadPeaksFile(path, columnCount);
    }

    public async Task<float[]?> GetOrGeneratePeaksAsync(
        string sourcePath,
        string itemId,
        double durationSeconds,
        int columnCount,
        CancellationToken cancellationToken = default)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath) || durationSeconds <= 0 || columnCount <= 0)
        {
            return null;
        }

        columnCount = FilmstripLayout.ClampFrameCount(columnCount);
        var cached = ReadCachedPeaks(itemId, durationSeconds, columnCount);
        if (cached is not null)
        {
            return cached;
        }

        ExternalToolLocator.Refresh();
        if (!ExternalToolLocator.IsFfmpegAvailable)
        {
            return null;
        }

        var samples = await DecodeMonoSamplesAsync(sourcePath, cancellationToken);
        if (samples is null || samples.Length == 0)
        {
            return null;
        }

        var peaks = BucketPeaks(samples, columnCount);
        if (peaks.Length == 0)
        {
            return null;
        }

        TryWriteCache(itemId, durationSeconds, columnCount, peaks);
        return peaks;
    }

    private static string GetCachePath(string itemId, double durationSeconds, int columnCount)
    {
        var durationKey = ((int)Math.Round(durationSeconds * 10)).ToString(CultureInfo.InvariantCulture);
        var fileName = $"{itemId}_{durationKey}_{columnCount}.bin";
        return Path.Combine(RootFolder, fileName);
    }

    private static float[]? ReadPeaksFile(string path, int columnCount)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < MinPeakBytes || bytes.Length % sizeof(float) != 0)
            {
                return null;
            }

            var peaks = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, peaks, 0, bytes.Length);
            if (peaks.Length != columnCount * 2)
            {
                return null;
            }

            return peaks;
        }
        catch
        {
            return null;
        }
    }

    private static void TryWriteCache(string itemId, double durationSeconds, int columnCount, float[] peaks)
    {
        try
        {
            Directory.CreateDirectory(RootFolder);
            var path = GetCachePath(itemId, durationSeconds, columnCount);
            var bytes = new byte[peaks.Length * sizeof(float)];
            Buffer.BlockCopy(peaks, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(path, bytes);
        }
        catch
        {
        }
    }

    private static async Task<float[]?> DecodeMonoSamplesAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExternalToolLocator.FfmpegExecutable,
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        foreach (var argument in new[]
        {
            "-hide_banner",
            "-loglevel", "error",
            "-i", sourcePath,
            "-vn",
            "-ac", "1",
            "-ar", SampleRateHz.ToString(CultureInfo.InvariantCulture),
            "-f", "f32le",
            "pipe:1",
        })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
            await using var stdout = process.StandardOutput.BaseStream;
            using var memory = new MemoryStream();
            await stdout.CopyToAsync(memory, cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0 || memory.Length < sizeof(float))
            {
                if (process.ExitCode != 0)
                {
                    AppDiagnostic.Log(
                        $"Waveform decode failed exit={process.ExitCode} err={TrimDiagnostic(stderr)}");
                }

                return null;
            }

            var byteCount = (int)memory.Length - (int)(memory.Length % sizeof(float));
            if (byteCount < sizeof(float))
            {
                return null;
            }

            var samples = new float[byteCount / sizeof(float)];
            Buffer.BlockCopy(memory.GetBuffer(), 0, samples, 0, byteCount);
            return samples;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostic.Log($"Waveform decode exception: {ex.Message}");
            return null;
        }
    }

    private static float[] BucketPeaks(float[] samples, int columnCount)
    {
        if (samples.Length == 0 || columnCount <= 0)
        {
            return [];
        }

        var peaks = new float[columnCount * 2];
        var samplesPerColumn = (double)samples.Length / columnCount;
        var maxAbs = 0f;

        for (var column = 0; column < columnCount; column++)
        {
            var start = (int)Math.Floor(column * samplesPerColumn);
            var end = (int)Math.Floor((column + 1) * samplesPerColumn);
            if (end <= start)
            {
                end = Math.Min(start + 1, samples.Length);
            }

            end = Math.Min(end, samples.Length);
            start = Math.Clamp(start, 0, samples.Length - 1);

            var min = float.PositiveInfinity;
            var max = float.NegativeInfinity;
            for (var i = start; i < end; i++)
            {
                var value = samples[i];
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }
            }

            if (!float.IsFinite(min) || !float.IsFinite(max))
            {
                min = 0;
                max = 0;
            }

            peaks[column * 2] = min;
            peaks[column * 2 + 1] = max;
            maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(min), Math.Abs(max)));
        }

        if (maxAbs > 0)
        {
            for (var i = 0; i < peaks.Length; i++)
            {
                peaks[i] /= maxAbs;
            }
        }

        return peaks;
    }

    private static string TrimDiagnostic(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "(no stderr)";
        }

        var detail = stderr.Trim();
        return detail.Length > 240 ? detail[..240] + "..." : detail;
    }
}
