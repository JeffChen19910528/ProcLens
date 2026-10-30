namespace ProcLens.Core.Dumps;

/// <summary>Both policies from the machine policy file, after deciding whether the file may be believed at all.</summary>
/// <param name="Path">Where the policy was looked for.</param>
/// <param name="Overridden">True if <c>PROCLENS_POLICY</c> chose the path: the operator's own explicit decision, so it is trusted.</param>
/// <param name="IgnoredBecause">Set when a file exists but is not protected; both policies then fall back to the safe defaults.</param>
public sealed record MachinePolicy(
    string Path, bool Overridden, string? IgnoredBecause, bool Exists, AdminDumpPolicy Admin, RetentionPolicy Retention)
{
    public static MachinePolicy Load() => Load(ProcLensPaths.PolicyFile, ProcLensPaths.PolicyFileIsOverridden);

    public static MachinePolicy Load(string path, bool overridden)
    {
        var exists = File.Exists(path);
        var untrusted = overridden ? null : PolicyFileTrust.WhyUntrusted(path);

        if (untrusted is not null)
        {
            var source = $"{path} (ignored: {untrusted}; see docs/SECURITY.md, \"Protecting the policy file\")";
            return new MachinePolicy(path, overridden, untrusted, exists,
                AdminDumpPolicy.Disabled(source), new RetentionPolicy(Source: $"defaults ({source})"));
        }

        return new MachinePolicy(path, overridden, null, exists, AdminDumpPolicy.Load(path), RetentionPolicy.Load(path));
    }
}
