using ProcLens.Core.Platform;

namespace ProcLens.Application;

internal static class CapabilityGuard
{
    public static void Require(ICapabilityRegistry capabilities, Capability capability)
    {
        if (capabilities.IsSupported(capability)) return;
        var reason = capabilities.All.First(c => c.Capability == capability).Reason;
        throw new FeatureUnavailableException(capability, reason);
    }
}
