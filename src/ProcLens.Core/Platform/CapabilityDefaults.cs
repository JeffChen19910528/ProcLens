namespace ProcLens.Core.Platform;

public static class CapabilityDefaults
{
    /// <summary>
    /// Process, CPU, memory and thread inspection are supported. Module inspection is supported unless
    /// <paramref name="moduleUnsupportedReason"/> is given, in which case it is declared not supported with that reason.
    /// </summary>
    public static IReadOnlyList<CapabilityInfo> InspectionAndMonitoring(string? moduleUnsupportedReason = null) =>
    [
        new(Capability.ProcessInspection, SupportStatus.Supported),
        new(Capability.CpuMonitoring, SupportStatus.Supported),
        new(Capability.MemoryMonitoring, SupportStatus.Supported),
        new(Capability.ThreadInspection, SupportStatus.Supported),
        moduleUnsupportedReason is null
            ? new(Capability.ModuleInspection, SupportStatus.Supported)
            : new(Capability.ModuleInspection, SupportStatus.NotSupported, moduleUnsupportedReason),
    ];
}
