using Microsoft.UI.Xaml.Media.Imaging;

namespace TrimFetch.Helpers;

internal static class AppIconHelper
{
    public static BitmapImage CreatePackagedImage(string msAppxUri) => new(new Uri(msAppxUri));
}
