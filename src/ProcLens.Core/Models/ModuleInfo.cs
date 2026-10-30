namespace ProcLens.Core.Models;

public sealed record ModuleInfo(
    string Name,
    string? Path,
    Measurement<ulong> BaseAddress,
    Measurement<long> Size);
