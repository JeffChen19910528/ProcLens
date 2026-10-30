using Microsoft.Extensions.DependencyInjection;
using ProcLens.Core.Abstractions;

namespace ProcLens.Platform.Linux;

/// <summary>Everything the Linux platform contributes: process inspection, and an explicit "dump is not supported".</summary>
public static class LinuxPlatformModule
{
    public static IServiceCollection AddLinuxPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformProcessProvider>(_ => new LinuxProcessProvider());
        services.AddSingleton<IProcessDumpProvider, LinuxProcessDumpProvider>();
        return services;
    }
}
