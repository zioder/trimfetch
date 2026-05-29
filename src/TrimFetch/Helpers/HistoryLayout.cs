namespace TrimFetch.Helpers;

public static class HistoryLayout
{
    public const int MaxVisibleRows = 5;

    public const double RowHeightDip = 64;

    public static double ListHeightForItemCount(int itemCount) =>
        Math.Clamp(itemCount, 1, MaxVisibleRows) * RowHeightDip;
}
