using ProcLens.Core.Abstractions;
using ProcLens.Core.Platform;

namespace ProcLens.Platform.Linux;

/// <summary>Process dump is intentionally unsupported on Linux; no native dump mechanism is ever invoked.</summary>
public sealed class LinuxProcessDumpProvider : IProcessDumpProvider
{
    public PlatformKind Platform => PlatformKind.Linux;
    public bool IsSupported => false;

    public Task<DumpResult> CreateDumpAsync(DumpRequest request, CancellationToken ct = default) =>
        Task.FromResult(new DumpResult(
            DumpStatus.PlatformNotSupported,
            Message: "Process dump is not supported on this operating system.\nSupported platform: Windows."));
}

