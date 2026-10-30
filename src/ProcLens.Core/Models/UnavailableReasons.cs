namespace ProcLens.Core.Models;

public static class UnavailableReasons
{
    public const string CpuNotSampled = "CPU usage needs two samples; use 'process monitor' or 'process list --sort cpu'.";
    public const string AccessDenied = "Access denied or process exited.";
}
