using Microsoft.Extensions.DependencyInjection;
using ProcLens.Core.Abstractions;

namespace ProcLens.Platform.MacOS;

/// <summary>Everything the macOS platform contributes: process inspection, and an explicit "dump is not supported".</summary>
public static class MacOSPlatformModule
{
    public static IServiceCollection AddMacOSPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformProcessProvider, MacOSProcessProvider>();
        services.AddSingleton<IProcessDumpProvider, MacOSProcessDumpProvider>();
        return services;
    }
}
