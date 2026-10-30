using Microsoft.Extensions.DependencyInjection;
using ProcLens.Application.Abstractions;
using ProcLens.Core.Dumps;
using ProcLens.Application.Dumps;
using ProcLens.Application.Monitoring;
using ProcLens.Application.Processes;
using ProcLens.Application.Reports;

namespace ProcLens.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProcLensApplication(this IServiceCollection services) =>
        services.AddSingleton(TimeProvider.System)
            .AddSingleton<IDumpFinalizer, DumpFinalizer>()
            .AddSingleton<IProcessQueryService, ProcessQueryService>()
            .AddSingleton<IProcessMonitor, ProcessMonitor>()
            .AddSingleton<IDumpService, DumpService>()
            .AddSingleton<IDumpVaultService, DumpVaultService>()
            .AddSingleton<IDumpRetentionService, DumpRetentionService>()
            .AddSingleton<IReportService, ReportService>();
}

