using FluentAssertions;
using ProcLens.Core.Abstractions;
using ProcLens.Platform.Linux;

namespace ProcLens.Linux.Tests;

public class LinuxDumpProviderTests
{
    [Fact]
    public async Task Dump_request_is_rejected_as_not_supported()
    {
        var provider = new LinuxProcessDumpProvider();
        var result = await provider.CreateDumpAsync(new DumpRequest(1, DumpType.Mini, "."));

        provider.IsSupported.Should().BeFalse();
        result.Status.Should().Be(DumpStatus.PlatformNotSupported);
        result.FilePath.Should().BeNull();
    }
}
