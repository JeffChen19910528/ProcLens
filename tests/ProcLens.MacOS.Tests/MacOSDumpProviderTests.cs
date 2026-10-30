using FluentAssertions;
using ProcLens.Core.Abstractions;
using ProcLens.Platform.MacOS;

namespace ProcLens.MacOS.Tests;

public class MacOSDumpProviderTests
{
    [Fact]
    public async Task Dump_request_is_rejected_as_not_supported()
    {
        var provider = new MacOSProcessDumpProvider();
        var result = await provider.CreateDumpAsync(new DumpRequest(1, DumpType.Mini, "."));

        provider.IsSupported.Should().BeFalse();
        result.Status.Should().Be(DumpStatus.PlatformNotSupported);
    }
}
