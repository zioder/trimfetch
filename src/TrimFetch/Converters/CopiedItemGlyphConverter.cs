using Microsoft.UI.Xaml.Data;

namespace TrimFetch.Converters;

public sealed class CopiedItemGlyphConverter : IValueConverter
{
    private const string CopyGlyph = "\uE8C8";
    private const string CheckGlyph = "\uE73E";

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var copiedId = value as string;
        var itemId = parameter as string;
        return copiedId is not null && itemId is not null && copiedId == itemId
            ? CheckGlyph
            : CopyGlyph;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
