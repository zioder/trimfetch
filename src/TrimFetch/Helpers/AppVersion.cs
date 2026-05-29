using System.Reflection;
using Windows.ApplicationModel;

namespace TrimFetch.Helpers;

/// <summary>
/// App version for MSIX (package identity) and unpackaged (assembly) installs.
/// </summary>
internal static class AppVersion
{
    public static Version GetVersion()
    {
        try
        {
            var packageVersion = Package.Current.Id.Version;
            return new Version(packageVersion.Major, packageVersion.Minor, packageVersion.Build, packageVersion.Revision);
        }
        catch
        {
            return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);
        }
    }

    public static string GetDisplayLabel()
    {
        var version = GetVersion();
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
