using FluentAssertions;
using ProcLens.Core.Models;
using ProcLens.Core.Platform;
using ProcLens.Platform.MacOS;

namespace ProcLens.MacOS.Tests;

public class MacOSCapabilityTests
{
    [Fact]
    public async Task Module_inspection_is_declared_unsupported_with_a_reason_and_never_returns_an_empty_list()
    {
        var provider = new MacOSProcessProvider();

        var capability = provider.GetCapabilities().Single(c => c.Capability == Capability.ModuleInspection);
        var result = await provider.GetModulesAsync(1);

        capability.Status.Should().Be(SupportStatus.NotSupported);
        capability.Reason.Should().NotBeNullOrEmpty();
        result.Status.Should().Be(InspectionStatus.NotSupported);
    }
}
