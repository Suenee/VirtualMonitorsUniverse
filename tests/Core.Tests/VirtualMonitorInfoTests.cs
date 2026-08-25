using Microsoft.VisualStudio.TestTools.UnitTesting;
using VirtualMonitorsUniverse.Core;

namespace VirtualMonitorsUniverse.Core.Tests;

[TestClass]
public sealed class VirtualMonitorInfoTests
{
    [TestMethod]
    public void RecordPreservesDisplayGeometry()
    {
        var monitor = new VirtualMonitorInfo(
            "test-monitor",
            @"\\.\DISPLAY99",
            @"ROOT\DISPLAY\0000",
            true,
            1920,
            1080,
            64,
            -1080);

        Assert.AreEqual(1920, monitor.Width);
        Assert.AreEqual(1080, monitor.Height);
        Assert.AreEqual(64, monitor.X);
        Assert.AreEqual(-1080, monitor.Y);
    }
}
