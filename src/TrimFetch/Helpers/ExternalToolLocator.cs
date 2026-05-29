using System.Diagnostics;

namespace TrimFetch.Helpers;

/// <summary>
/// Resolves ffmpeg/yt-dlp to full paths so thumbnail generation works when the app
/// is launched without a login shell PATH (WinGet shims, IDE run, packaged MSIX, etc.).
/// </summary>
public static class ExternalToolLocator
{
    private static readonly object RefreshLock = new();

    private static string? _ffmpegPath;
    private static string? _ffprobePath;
    private static string? _ytDlpPath;
    private static bool _pathsResolved;

    public static string FfmpegExecutable => _ffmpegPath ?? "ffmpeg";

    public static string FfprobeExecutable => _ffprobePath ?? "ffprobe";

    public static string YtDlpExecutable => _ytDlpPath ?? "yt-dlp";

    public static bool IsFfmpegAvailable => !string.IsNullOrEmpty(_ffmpegPath);

    public static bool IsYtDlpAvailable => !string.IsNullOrEmpty(_ytDlpPath);

    /// <summary>Clears cached paths so the next <see cref="Refresh"/> re-resolves from disk/PATH.</summary>
    public static void Invalidate()
    {
        lock (RefreshLock)
        {
            _pathsResolved = false;
            _ffmpegPath = null;
            _ffprobePath = null;
            _ytDlpPath = null;
        }
    }

    /// <summary>
    /// Resolves tool executables once per session. Call <see cref="Invalidate"/> after install
    /// or when a subprocess fails because tools moved off PATH.
    /// </summary>
    public static void Refresh()
    {
        lock (RefreshLock)
        {
            RefreshProcessPath();
            if (_pathsResolved)
            {
                return;
            }

            _ffmpegPath = ResolveExecutable("ffmpeg");
            _ffprobePath = ResolveExecutable("ffprobe");
            _ytDlpPath = ResolveExecutable("yt-dlp");
            _pathsResolved = true;
        }
    }

    public static void RefreshProcessPath()
    {
        var machinePath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? string.Empty;
        var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
        Environment.SetEnvironmentVariable("Path", $"{machinePath};{userPath}", EnvironmentVariableTarget.Process);
    }

    private static string? ResolveExecutable(string tool)
    {
        string? wingetPackageBin = null;
        string? other = null;
        string? wingetLink = null;

        foreach (var candidate in EnumerateCandidates(tool))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (IsWinGetPackageBinary(candidate))
            {
                wingetPackageBin ??= candidate;
                continue;
            }

            if (IsWinGetLink(candidate))
            {
                wingetLink ??= candidate;
                continue;
            }

            other ??= candidate;
        }

        return wingetPackageBin ?? other ?? wingetLink;
    }

    private static bool IsWinGetLink(string path) =>
        path.Contains(@"\Microsoft\WinGet\Links\", StringComparison.OrdinalIgnoreCase);

    private static bool IsWinGetPackageBinary(string path) =>
        path.Contains(@"\Microsoft\WinGet\Packages\", StringComparison.OrdinalIgnoreCase)
        && path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateCandidates(string tool)
    {
        var fileName = tool.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? tool : $"{tool}.exe";

        var fromWhere = ResolveFromWhere(tool);
        if (!string.IsNullOrWhiteSpace(fromWhere))
        {
            yield return fromWhere;
        }

        foreach (var localAppData in EnumerateLocalAppDataRoots())
        {
            yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", fileName);

            var wingetRoot = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (!Directory.Exists(wingetRoot))
            {
                continue;
            }

            foreach (var packageDir in Directory.EnumerateDirectories(wingetRoot))
            {
                if (!packageDir.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase)
                    && !packageDir.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var binDir in Directory.EnumerateDirectories(packageDir, "bin", SearchOption.AllDirectories))
                {
                    yield return Path.Combine(binDir, fileName);
                }
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "ffmpeg", "bin", fileName);

    }

    /// <summary>
    /// Packaged apps redirect <see cref="Environment.SpecialFolder.LocalApplicationData"/>;
    /// WinGet/ffmpeg live in the real user profile.
    /// </summary>
    private static IEnumerable<string> EnumerateLocalAppDataRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in new[]
                 {
                     Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                     Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         "AppData",
                         "Local"),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(Path.GetFullPath(path)))
            {
                yield return Path.GetFullPath(path);
            }
        }
    }

    private static string? ResolveFromWhere(string tool)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    ArgumentList = { tool },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            if (process.ExitCode != 0)
            {
                return null;
            }

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }
}
