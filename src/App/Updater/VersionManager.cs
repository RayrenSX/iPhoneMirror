using System.Reflection;

namespace IPhoneMirror.App.Updater;

internal static class VersionManager
{
    internal static SemanticVersion Current
    {
        get
        {
            var assembly = typeof(VersionManager).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (SemanticVersion.TryParse(informational, out var semantic))
                return semantic;
            var version = assembly.GetName().Version ?? new Version(0, 0, 0);
            return new SemanticVersion(version.Major, version.Minor,
                Math.Max(0, version.Build));
        }
    }

    internal static string DisplayVersion => $"v{Current}";
}
