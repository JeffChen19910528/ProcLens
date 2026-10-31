using ProcLens.Core.Platform;

namespace ProcLens.Application;

/// <summary>Raised when a requested feature is not available on this platform or build.</summary>
public sealed class FeatureUnavailableException(Capability capability, string? reason)
    : Exception($"{capability} is not available: {reason ?? "no reason given"}")
{
    public Capability Capability { get; } = capability;
    public string? Reason { get; } = reason;
}
