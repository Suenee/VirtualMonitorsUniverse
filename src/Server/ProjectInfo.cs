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
            if (!string.IsNullOrWhiteSpace(informational)) return Normalize(informational.Split('+')[0]);
            return Normalize(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.00");
        }
    }

    private static string Normalize(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 3 && parts[2] == "0" ? $"{parts[0]}.{parts[1]}" : value;
    }
}
