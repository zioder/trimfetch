using Microsoft.UI.Xaml.Data;
using TrimFetch.Helpers;

namespace TrimFetch.Converters;

public sealed class ThumbnailPathConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string raw || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var decodeWidth = 240;
        if (parameter is string widthText && int.TryParse(widthText, out var parsed) && parsed > 0)
        {
            decodeWidth = parsed;
        }

        return ImageFileSource.FromPath(raw, decodeWidth);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
