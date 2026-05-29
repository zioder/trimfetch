using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.ApplicationModel.DataTransfer;
using TrimFetch.Helpers;

namespace TrimFetch.Services;

public static partial class ClipboardUrlReader
{
    private const int MaxClipboardLength = 4096;
    private const int MaxUrlLength = 2048;
    private const uint CfUnicodeText = 13;

    public static Task<string?> TryGetTextAsync() =>
        UiDispatcher.InvokeAsync(ReadClipboardOnUiThread);

    private static async Task<string?> ReadClipboardOnUiThread()
    {
        try
        {
            var fromWinRt = await TryReadWinRtClipboardAsync();
            if (!string.IsNullOrWhiteSpace(fromWinRt))
            {
                return TrimClipboardText(fromWinRt);
            }

            var fromWin32 = TryReadWin32UnicodeText();
            return string.IsNullOrWhiteSpace(fromWin32) ? null : TrimClipboardText(fromWin32);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryReadWinRtClipboardAsync()
    {
        var package = Clipboard.GetContent();
        if (package is null)
        {
            return null;
        }

        if (package.Contains(StandardDataFormats.Text))
        {
            var text = await package.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        if (package.Contains(StandardDataFormats.WebLink))
        {
            var link = await package.GetWebLinkAsync();
            if (link is not null && !string.IsNullOrWhiteSpace(link.AbsoluteUri))
            {
                return link.AbsoluteUri;
            }
        }

        if (package.Contains(StandardDataFormats.Html))
        {
            var html = await package.GetHtmlFormatAsync();
            var fromHtml = ExtractUrlFromHtml(html);
            if (!string.IsNullOrWhiteSpace(fromHtml))
            {
                return fromHtml;
            }
        }

        return null;
    }

    private static string? TryReadWin32UnicodeText()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return null;
        }

        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static string? ExtractUrlFromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var hrefMatch = HtmlHrefRegex().Match(html);
        if (hrefMatch.Success)
        {
            return Uri.UnescapeDataString(hrefMatch.Groups[1].Value);
        }

        var match = HttpUrlRegex().Match(html);
        return match.Success ? match.Value : null;
    }

    private static string TrimClipboardText(string text)
    {
        text = text.Trim();
        return text.Length > MaxClipboardLength ? text[..MaxClipboardLength] : text;
    }

    public static bool TryExtractHttpUrl(string clipboardText, out string url)
    {
        url = string.Empty;
        var trimmed = clipboardText.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (LooksLikeHttpUrl(trimmed) && IsSingleUrlCandidate(trimmed))
        {
            url = trimmed;
            return true;
        }

        var match = HttpUrlRegex().Match(trimmed);
        if (!match.Success)
        {
            return false;
        }

        var candidate = match.Value.TrimEnd('.', ',', ';', ')', ']', '"', '\'');
        if (!LooksLikeHttpUrl(candidate))
        {
            return false;
        }

        url = candidate;
        return true;
    }

    private static bool LooksLikeHttpUrl(string value)
    {
        if (value.Length > MaxUrlLength)
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host) || uri.Host.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool IsSingleUrlCandidate(string value)
    {
        if (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
        {
            return false;
        }

        return HttpUrlRegex().Matches(value).Count <= 1;
    }

    [GeneratedRegex(@"href\s*=\s*[""'](https?://[^""']+)[""']", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlHrefRegex();

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrlRegex();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);
}
