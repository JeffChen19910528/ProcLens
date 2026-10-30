namespace ProcLens.Core.Models;

/// <summary>The four memory measures are deliberately separate; any may be unavailable on a platform.</summary>
public sealed record MemoryStats(
    Measurement<long> WorkingSet,
    Measurement<long> PrivateMemory,
    Measurement<long> VirtualMemory,
    Measurement<long> PeakWorkingSet);

/// <summary>PercentOfOneCore can exceed 100 for multi-threaded processes; PercentOfMachine divides by logical processors.</summary>
public readonly record struct CpuUsageSample(double PercentOfOneCore, double PercentOfMachine);

public sealed record DiagnosticWarning(string Code, string Message);
