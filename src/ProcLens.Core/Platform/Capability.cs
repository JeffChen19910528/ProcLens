namespace ProcLens.Core.Platform;

public enum Capability
{
    ProcessInspection,
    CpuMonitoring,
    MemoryMonitoring,
    ThreadInspection,
    ModuleInspection,
    ProcessDump,
}

public enum SupportStatus
{
    Supported,
    NotSupported,
}

public sealed record CapabilityInfo(Capability Capability, SupportStatus Status, string? Reason = null);
