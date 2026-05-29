using System.Runtime.InteropServices;

namespace TrimFetch.Helpers;

public static class WindowRegionHelper
{
    private const int RgnOr = 2;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    public readonly record struct RegionRect(int X, int Y, int Width, int Height, int CornerRadius);

    public static void Apply(IntPtr hwnd, IReadOnlyList<RegionRect> regions)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (regions.Count == 0)
        {
            _ = SetWindowRgn(hwnd, IntPtr.Zero, true);
            return;
        }

        var combined = CreateRectRgn(0, 0, 0, 0);
        if (combined == IntPtr.Zero)
        {
            return;
        }

        var added = false;

        try
        {
            foreach (var region in regions)
            {
                if (region.Width <= 0 || region.Height <= 0)
                {
                    continue;
                }

                var corner = Math.Max(0, region.CornerRadius);
                // GDI uses exclusive right/bottom; +1 avoids flat bottom corners on short rects.
                var card = CreateRoundRectRgn(
                    region.X,
                    region.Y,
                    region.X + region.Width + 1,
                    region.Y + region.Height + 1,
                    corner * 2,
                    corner * 2);

                if (card == IntPtr.Zero)
                {
                    continue;
                }

                _ = CombineRgn(combined, combined, card, RgnOr);
                DeleteObject(card);
                added = true;
            }

            if (!added)
            {
                DeleteObject(combined);
                _ = SetWindowRgn(hwnd, IntPtr.Zero, true);
                return;
            }

            if (SetWindowRgn(hwnd, combined, true) == 0)
            {
                DeleteObject(combined);
            }

            // On success Windows owns combined; do not DeleteObject.
        }
        catch
        {
            DeleteObject(combined);
        }
    }

    public static void Clear(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            _ = SetWindowRgn(hwnd, IntPtr.Zero, true);
        }
    }

    /// <summary>
    /// Clips the entire window away (empty region) so a freshly-activated, backdrop-less
    /// overlay paints nothing instead of flashing a black rectangle before its first reveal.
    /// </summary>
    public static void Collapse(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var empty = CreateRectRgn(0, 0, 0, 0);
        if (empty == IntPtr.Zero)
        {
            return;
        }

        if (SetWindowRgn(hwnd, empty, true) == 0)
        {
            DeleteObject(empty);
        }
    }
}
