using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcLens.Application;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Audit;
using ProcLens.Core.Dumps;
using ProcLens.Core.Platform;
using ProcLens.Infrastructure;
using ProcLens.Infrastructure.Audit;
using ProcLens.Platform.Linux;
using ProcLens.Platform.MacOS;
using ProcLens.Platform.Windows;

namespace ProcLens.Cli;

/// <summary>
/// Composition root. It wires the layers together and picks the platform module for the current operating system;
/// it never constructs a platform type itself, so platform internals stay inside their own projects.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddProcLens(this IServiceCollection services, PlatformKind? platformOverride = null)
    {
        var platform = platformOverride ?? new PlatformDetector().Current;

        services.AddLogging(b => b
            .SetMinimumLevel(LogLevel.Warning)
            .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)); // keep stdout clean for --json
        services.AddSingleton<IPlatformDetector, PlatformDetector>();
        services.AddProcLensApplication();
        services.AddAccountabilityAndPolicy();
        services.AddPlatform(platform);
        services.AddSingleton<ICapabilityRegistry>(sp => BuildCapabilityRegistry(sp, platform));
        return services;
    }

    /// <summary>Who is acting, what is recorded, and which policy applies. Identical on every platform.</summary>
    private static void AddAccountabilityAndPolicy(this IServiceCollection services)
    {
        services.AddSingleton<IOperatorContext, SystemOperatorContext>();
        services.AddSingleton(_ => new FileAuditLog(ProcLensPaths.AuditLog));
        services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<FileAuditLog>());

        // The policy file is only believed if an ordinary user could not have planted or edited it (see PolicyFileTrust).
        services.AddSingleton(_ => MachinePolicy.Load());
        services.AddSingleton(sp => sp.GetRequiredService<MachinePolicy>().Admin);
        services.AddSingleton(sp => sp.GetRequiredService<MachinePolicy>().Retention);
        services.AddSingleton<IDumpAuthorizer>(sp => new AdminAwareDumpAuthorizer(
            sp.GetRequiredService<IOperatorContext>(), sp.GetRequiredService<AdminDumpPolicy>()));
    }

    private static void AddPlatform(this IServiceCollection services, PlatformKind platform)
    {
        switch (platform)
        {
            case PlatformKind.Windows when OperatingSystem.IsWindows():
                services.AddWindowsPlatform();
                break;
            case PlatformKind.Linux:
                services.AddLinuxPlatform();
                break;
            case PlatformKind.MacOS:
                services.AddMacOSPlatform();
                break;
            default:
                throw new PlatformNotSupportedException("This operating system is not supported by ProcLens.");
        }
    }

    /// <summary>What the platform's provider declares, plus whether dumps exist here, as one registry the rest of the program asks.</summary>
    private static CapabilityRegistry BuildCapabilityRegistry(IServiceProvider services, PlatformKind platform)
    {
        var declared = services.GetRequiredService<IPlatformProcessProvider>().GetCapabilities().ToList();
        var dump = services.GetService<IProcessDumpProvider>();
        if (dump is { IsSupported: true })
            declared.Add(new CapabilityInfo(Capability.ProcessDump, SupportStatus.Supported));
        else if (dump is { IsSupported: false })
            declared.Add(new CapabilityInfo(Capability.ProcessDump, SupportStatus.NotSupported,
                "Process dump is not supported on this operating system. Supported platform: Windows."));
        return new CapabilityRegistry(platform, declared);
    }
}
