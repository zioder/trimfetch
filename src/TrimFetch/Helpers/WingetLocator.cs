using System.Diagnostics;

namespace TrimFetch.Helpers;

internal static class WingetLocator
{
    public const string AppInstallerStoreUri = "ms-windows-store://pdp/?productid=9NBLGGH4NNS1";

    public const string AppInstallerDownloadUri = "https://aka.ms/getwinget";

    public static string? ResolveExecutablePath()
    {
        ExternalToolLocator.RefreshProcessPath();

        foreach (var candidate in EnumerateCandidatePaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var executable = ResolveExecutablePath();
        if (executable is null)
        {
            return false;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    ArgumentList = { "--version" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static IEnumerable<string> EnumerateCandidatePaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void Add(HashSet<string> set, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            set.Add(path);
        }

        var pathEnv = Environment.GetEnvironmentVariable("Path") ?? string.Empty;
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Add(seen, Path.Combine(dir, "winget.exe"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Add(seen, Path.Combine(localAppData, @"Microsoft\WindowsApps\winget.exe"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var windowsApps = Path.Combine(programFiles, "WindowsApps");
        if (Directory.Exists(windowsApps))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(windowsApps, "Microsoft.DesktopAppInstaller_*"))
                {
                    Add(seen, Path.Combine(dir, "winget.exe"));
                }
            }
            catch
            {
                // WindowsApps may be inaccessible without elevation.
            }
        }

        return seen;
    }
}
