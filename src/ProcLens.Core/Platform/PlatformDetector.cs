namespace ProcLens.Core.Platform;

public interface IPlatformDetector
{
    PlatformKind Current { get; }
}

public sealed class PlatformDetector : IPlatformDetector
{
    public PlatformKind Current { get; } =
        OperatingSystem.IsWindows() ? PlatformKind.Windows :
        OperatingSystem.IsLinux() ? PlatformKind.Linux :
        OperatingSystem.IsMacOS() ? PlatformKind.MacOS :
        PlatformKind.Unknown;
}
