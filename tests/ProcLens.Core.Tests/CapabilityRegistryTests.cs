using FluentAssertions;
using ProcLens.Core.Platform;

namespace ProcLens.Core.Tests;

public class CapabilityRegistryTests
{
    [Fact]
    public void Undeclared_capability_is_not_supported()
    {
        var registry = new CapabilityRegistry(PlatformKind.Linux, new[]
        {
            new CapabilityInfo(Capability.ProcessInspection, SupportStatus.Supported),
        });

        registry.IsSupported(Capability.ProcessInspection).Should().BeTrue();
        registry.IsSupported(Capability.ProcessDump).Should().BeFalse();
    }

    [Fact]
    public void All_lists_every_capability()
    {
        var registry = new CapabilityRegistry(PlatformKind.Windows, Array.Empty<CapabilityInfo>());
        registry.All.Should().HaveCount(Enum.GetValues<Capability>().Length);
    }

    [Fact]
    public void Detector_reports_a_known_platform()
    {
        new PlatformDetector().Current.Should().NotBe(PlatformKind.Unknown);
    }
}
