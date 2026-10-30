namespace ProcLens.Core.Models;

public enum AccessStatus
{
    Full,
    Partial,
    Denied,
}

/// <summary>A measurement that is either available or explicitly not — never a silent zero.</summary>
public readonly record struct Measurement<T>(bool IsAvailable, T? Value, string? UnavailableReason = null)
    where T : struct
{
    public static Measurement<T> Of(T value) => new(true, value);
    public static Measurement<T> Unavailable(string reason) => new(false, null, reason);
}
