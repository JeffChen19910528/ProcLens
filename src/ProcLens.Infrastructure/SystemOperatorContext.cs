using System.Security.Principal;
using ProcLens.Core.Dumps;

namespace ProcLens.Infrastructure;

public sealed class SystemOperatorContext : IOperatorContext
{
    public string Identity { get; } = OperatingSystem.IsWindows() ? WindowsName() : Environment.UserName;

    /// <summary>True when this very process is elevated (Windows) or privileged (root). ProcLens never changes this.</summary>
    public bool IsElevated => Environment.IsPrivilegedProcess;

    public string Machine => Environment.MachineName;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string WindowsName() => WindowsIdentity.GetCurrent().Name;
}
