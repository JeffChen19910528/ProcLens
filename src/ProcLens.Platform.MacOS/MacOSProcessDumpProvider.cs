using ProcLens.Core.Abstractions;
using ProcLens.Core.Platform;

namespace ProcLens.Platform.MacOS;

/// <summary>Process dump is intentionally unsupported on macOS; no native dump mechanism is ever invoked.</summary>
public sealed class MacOSProcessDumpProvider : IProcessDumpProvider
{
    public PlatformKind Platform => PlatformKind.MacOS;
    public bool IsSupported => false;

    public Task<DumpResult> CreateDumpAsync(DumpRequest request, CancellationToken ct = default) =>
        Task.FromResult(new DumpResult(
            DumpStatus.PlatformNotSupported,
            Message: "Process dump is not supported on this operating system.\nSupported platform: Windows."));
}

