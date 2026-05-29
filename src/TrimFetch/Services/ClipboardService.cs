using System.Diagnostics;
using TrimFetch.Helpers;

namespace TrimFetch.Services;

public sealed class ClipboardService
{
    public Task CopyFileAsync(string path) =>
        UiDispatcher.InvokeAsync(async () => await CopyFileOnUiThreadAsync(path));

    private static async Task CopyFileOnUiThreadAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Nothing to copy.", fullPath);
        }

        try
        {
            ShellFileClipboard.CopyFiles([fullPath]);
            return;
        }
        catch (Exception shellError)
        {
            Debug.WriteLine($"Shell clipboard copy failed: {shellError}");
        }

        await CopyFileViaPowerShellAsync(fullPath);
    }

    private static async Task CopyFileViaPowerShellAsync(string fullPath)
    {
        var escaped = fullPath.Replace("'", "''");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -STA -Command \"Set-Clipboard -LiteralPath '{escaped}'\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Clipboard copy failed.");
        }
    }

    private static void ActivateAppWindow()
    {
        try
        {
            if (App.Window is MainWindow window)
            {
                window.Activate();
            }

            NativeWindowForeground.BringToForeground(App.WindowHandle);
        }
        catch
        {
        }
    }

    public Task CopyTextAsync(string text) =>
        UiDispatcher.InvokeAsync(async () => CopyTextOnUiThread(text));

    private static void CopyTextOnUiThread(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage
        {
            RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
        };
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

        try
        {
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }
        catch
        {
        }
    }
}
