using VirtualMonitorsUniverse.Core;

namespace VirtualMonitorsUniverse.Cli;

/// <summary>
/// Compatibility wrapper for the final ALPHA self-test. The validated node
/// lifecycle now lives in Core so CLI, tray and Web Client share one implementation.
/// </summary>
internal static class AlphaSelfTestVdd
{
    private static readonly WindowsVddNodeService Service = new();

    public static PreparedPayload Prepare() => new(Service.PreparePayload());

    public static void InstallOne(PreparedPayload payload) => Service.InstallOne(payload.Inner);

    public static void RemoveOne(string instanceId) => Service.RemoveOne(instanceId);

    internal sealed class PreparedPayload : IDisposable
    {
        internal PreparedPayload(WindowsVddNodeService.PreparedPayload inner) => Inner = inner;
        internal WindowsVddNodeService.PreparedPayload Inner { get; }
        public void Dispose() => Inner.Dispose();
    }
}
