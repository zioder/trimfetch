namespace TrimFetch.Helpers;

#if DEBUG
internal static class SmokeTestLaunch
{
    public const string TrimVideoVariable = "TRIMFETCH_SMOKE_TRIM";
    public const string DownloadUrlVariable = "TRIMFETCH_SMOKE_DOWNLOAD";
    public const string TrimTimeoutVariable = "TRIMFETCH_SMOKE_TRIM_TIMEOUT";
    public const string LogPathVariable = "TRIMFETCH_SMOKE_LOG";

    public static bool IsEnabled =>
        IsDownloadSmokeEnabled
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TrimVideoVariable))
        || Environment.GetCommandLineArgs().Any(arg =>
            string.Equals(arg, "--smoke-trim", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--smoke-trim-video", StringComparison.OrdinalIgnoreCase));

    public static bool IsDownloadSmokeEnabled => !string.IsNullOrWhiteSpace(GetDownloadUrl());

    public static string? GetVideoPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable(TrimVideoVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--smoke-trim", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[i], "--smoke-trim-video", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = args[i + 1];
                if (!candidate.StartsWith('-') && File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    public static int GetTimeoutSeconds()
    {
        var fromEnv = Environment.GetEnvironmentVariable(TrimTimeoutVariable);
        if (int.TryParse(fromEnv, out var envSeconds) && envSeconds > 0)
        {
            return envSeconds;
        }

        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--smoke-trim-timeout", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var seconds)
                && seconds > 0)
            {
                return seconds;
            }
        }

        return 45;
    }

    public static string? GetLogPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable(LogPathVariable);
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }

    public static string? GetDownloadUrl()
    {
        var fromEnv = Environment.GetEnvironmentVariable(DownloadUrlVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--smoke-download", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = args[i + 1].Trim();
                if (!candidate.StartsWith('-'))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
#endif
