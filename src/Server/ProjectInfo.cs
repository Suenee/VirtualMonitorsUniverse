using System.Reflection;

namespace VirtualMonitorsUniverse.Server;

internal static class ProjectInfo
{
    public const string ProductName = "Virtual Monitors Universe";
    public const string RepositoryUrl = "https://github.com/Suenee/VirtualMonitorsUniverse";
    public const string DocumentationUrl = "https://github.com/Suenee/VirtualMonitorsUniverse/tree/devel/docs";
    public const string GuideUrl = "https://github.com/Suenee/VirtualMonitorsUniverse/blob/devel/README.md";

    public static string Version
    {
        get
        {
            var informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+')[0];
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(2) ?? "0.00";
        }
    }
}
