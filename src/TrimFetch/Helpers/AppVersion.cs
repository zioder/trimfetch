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
        if (TryParseLabel(GetInformationalVersionLabel(), out var informational))
        {
            return informational;
        }

        try
        {
            var packageVersion = Package.Current.Id.Version;
            return new Version(packageVersion.Major, packageVersion.Minor, packageVersion.Build, packageVersion.Revision);
        }
        catch
        {
            return NormalizeAssemblyVersion(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0));
        }
    }

    public static string GetDisplayLabel()
    {
        var informational = GetInformationalVersionLabel();
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        try
        {
            var packageVersion = Package.Current.Id.Version;
            return $"{packageVersion.Major}.{packageVersion.Minor}.{packageVersion.Build}";
        }
        catch
        {
            return FormatAssemblyVersionLabel(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0));
        }
    }

    private static string? GetInformationalVersionLabel()
    {
        var raw = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        var plus = trimmed.IndexOf('+');
        return plus >= 0 ? trimmed[..plus] : trimmed;
    }

    private static bool TryParseLabel(string? label, out Version version)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            version = new Version();
            return false;
        }

        return Version.TryParse(label, out version!);
    }

    private static string FormatAssemblyVersionLabel(Version version)
    {
        // Older builds used 1.0.0.x with patch in Revision.
        if (version.Revision > 0 && version.Build == 0)
        {
            return $"{version.Major}.{version.Minor}.{version.Revision}";
        }

        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static Version NormalizeAssemblyVersion(Version version)
    {
        if (version.Revision > 0 && version.Build == 0)
        {
            return new Version(version.Major, version.Minor, version.Revision);
        }

        return new Version(version.Major, version.Minor, version.Build);
    }
}
