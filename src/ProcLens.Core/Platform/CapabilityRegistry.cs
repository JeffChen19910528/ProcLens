namespace ProcLens.Core.Platform;

public interface ICapabilityRegistry
{
    PlatformKind Platform { get; }
    IReadOnlyList<CapabilityInfo> All { get; }
    bool IsSupported(Capability capability);
}

/// <summary>
/// Built from the capabilities each registered provider declares, so a feature is
/// only ever advertised if a provider on the current platform actually implements it.
/// </summary>
public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly Dictionary<Capability, CapabilityInfo> _map;

    public CapabilityRegistry(PlatformKind platform, IEnumerable<CapabilityInfo> declared)
    {
        Platform = platform;
        _map = Enum.GetValues<Capability>().ToDictionary(
            c => c,
            c => new CapabilityInfo(c, SupportStatus.NotSupported, "No provider declared this capability."));
        foreach (var info in declared)
            _map[info.Capability] = info;
    }

    public PlatformKind Platform { get; }

    public IReadOnlyList<CapabilityInfo> All => _map.Values.OrderBy(c => c.Capability).ToList();

    public bool IsSupported(Capability capability) =>
        _map[capability].Status == SupportStatus.Supported;
}
