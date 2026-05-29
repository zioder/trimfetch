using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace TrimFetch.Converters;

/// <summary>
/// Visible when the string is null or whitespace (placeholder state).
/// </summary>
public sealed class StringNullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isEmpty = value is not string text || string.IsNullOrWhiteSpace(text);
        var invert = parameter is string flag && flag.Equals("invert", StringComparison.OrdinalIgnoreCase);
        var visible = invert ? !isEmpty : isEmpty;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
