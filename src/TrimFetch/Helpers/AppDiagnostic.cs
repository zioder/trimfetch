namespace TrimFetch.Helpers;

internal static class AppDiagnostic
{
    private static string LogPath => Path.Combine(AppDataPaths.StorageRoot, "crash.log");

    public static void Log(string message)
    {
        var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
        AppendToLog(LogPath, line);

#if DEBUG
        var smokeLog = SmokeTestLaunch.GetLogPath();
        if (!string.IsNullOrWhiteSpace(smokeLog))
        {
            AppendToLog(smokeLog, line);
        }
#endif
    }

    private static void AppendToLog(string path, string line)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.AppendAllText(path, line);
        }
        catch
        {
        }
    }

    public static void LogException(string context, Exception ex) =>
        Log($"{context}: {ex}\n{ex.StackTrace}");
}
