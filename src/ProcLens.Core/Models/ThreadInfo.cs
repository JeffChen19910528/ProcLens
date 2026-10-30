namespace ProcLens.Core.Models;

public enum ThreadRunState
{
    Unknown,
    Running,
    Ready,
    Waiting,
    Sleeping,
    Stopped,
    Terminated,
}

/// <param name="Name">Thread name where the OS exposes one (Linux); null otherwise.</param>
/// <param name="StateDetail">Platform-specific detail, e.g. a Windows wait reason or the raw Linux state letter.</param>
public sealed record ThreadInfo(
    int ThreadId,
    string? Name,
    ThreadRunState State,
    string? StateDetail,
    Measurement<TimeSpan> CpuTime);
