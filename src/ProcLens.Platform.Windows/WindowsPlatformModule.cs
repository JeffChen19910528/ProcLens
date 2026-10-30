using Microsoft.Extensions.DependencyInjection;
using ProcLens.Core.Abstractions;
using ProcLens.Core.Dumps;
using ProcLens.Core.Dumps.Crypto;

namespace ProcLens.Platform.Windows;

/// <summary>
/// Everything the Windows platform contributes, registered in one place so the host needs to know only this
/// method, not the types behind it. It expects the host to provide <see cref="IDumpAuthorizer"/> and
/// <see cref="IDumpFinalizer"/> (platform-neutral policy and post-processing).
/// </summary>
public static class WindowsPlatformModule
{
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformProcessProvider, WindowsProcessProvider>();
        services.AddSingleton<IKekSource>(_ => new DpapiMachineKeySource());
        services.AddSingleton<IProcessDumpProvider>(sp => new WindowsProcessDumpProvider(
            authorizer: sp.GetRequiredService<IDumpAuthorizer>(),
            time: sp.GetService<TimeProvider>(),
            machineKey: sp.GetRequiredService<IKekSource>(),
            finalizer: sp.GetRequiredService<IDumpFinalizer>()));
        return services;
    }
}
