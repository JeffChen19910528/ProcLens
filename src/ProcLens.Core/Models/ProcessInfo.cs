using ProcLens.Core.Platform;

namespace ProcLens.Core.Models;

/// <remarks>
/// CpuUsage is a percentage of total machine capacity (all logical processors) and is only filled when
/// derived from two samples. CpuTime is the cumulative CPU time the OS reports. MemoryUsage is the working set.
/// </remarks>
public sealed record ProcessInfo(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    int? ParentProcessId,
    string? Owner,
    DateTimeOffset? StartTime,
    Measurement<double> CpuUsage,
    Measurement<TimeSpan> CpuTime,
    Measurement<long> MemoryUsage,
    Measurement<int> ThreadCount,
    PlatformKind Platform,
    AccessStatus AccessStatus);

/// <summary>PID plus start time; guards against PID reuse.</summary>
public readonly record struct ProcessIdentity(int ProcessId, DateTimeOffset StartTime);
