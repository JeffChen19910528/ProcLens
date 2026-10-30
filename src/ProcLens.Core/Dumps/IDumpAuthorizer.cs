namespace ProcLens.Core.Dumps;

/// <summary>An operator's explicit request to act on a process they do not own, with their justification.</summary>
public sealed record AdminAuthorization(string Reason);

/// <param name="TargetOwner">Null when the owner of the target could not be determined.</param>
public sealed record DumpAuthorizationContext(
    int ProcessId, string? TargetOwner, string OperatorIdentity, AdminAuthorization? Admin = null);

/// <param name="Policy">Which rule decided: "owner", "admin" or "denied". Recorded in the audit log.</param>
public sealed record AuthorizationDecision(bool Allowed, string Reason, string Policy = "");

/// <summary>Decides whether the operator may dump a process, independently of what the OS would permit.</summary>
public interface IDumpAuthorizer
{
    AuthorizationDecision Authorize(DumpAuthorizationContext context);
}

/// <summary>Who is running ProcLens and whether that process is already elevated. ProcLens never elevates itself.</summary>
public interface IOperatorContext
{
    string Identity { get; }
    bool IsElevated { get; }
    string Machine { get; }
}

/// <summary>
/// Policy that only lets an operator dump processes they own. If the owner cannot be established the request is
/// denied (fail closed).
/// </summary>
public sealed class SameUserDumpAuthorizer : IDumpAuthorizer
{
    public AuthorizationDecision Authorize(DumpAuthorizationContext context)
    {
        if (string.IsNullOrWhiteSpace(context.TargetOwner))
            return new(false, "The owner of the target process could not be determined, so the request was denied.", "denied");

        return string.Equals(context.TargetOwner, context.OperatorIdentity, StringComparison.OrdinalIgnoreCase)
            ? new(true, "The operator owns the target process.", "owner")
            : new(false, $"The target process is owned by '{context.TargetOwner}', not by the operator '{context.OperatorIdentity}'. " +
                         "Only processes you own can be dumped.", "denied");
    }
}

/// <summary>
/// Owned processes are always allowed. Processes owned by others need <b>all</b> of: an explicit administrative
/// authorization with a reason on the request, ProcLens already running elevated, a machine policy that enables
/// administrative dumps, and the operator being on that policy's allow-list (if it has one).
/// </summary>
public sealed class AdminAwareDumpAuthorizer(IOperatorContext operatorContext, AdminDumpPolicy policy) : IDumpAuthorizer
{
    private readonly SameUserDumpAuthorizer _owner = new();

    public AuthorizationDecision Authorize(DumpAuthorizationContext context)
    {
        var ownerDecision = _owner.Authorize(context);
        if (ownerDecision.Allowed) return ownerDecision;

        const string hint = " To dump another user's process use --authorize-admin with --reason, from an elevated session.";

        if (context.Admin is null)
            return new(false, ownerDecision.Reason + hint, "denied");

        if (!operatorContext.IsElevated)
            return new(false, "Administrative authorization requires ProcLens to be started elevated (as Administrator). ProcLens never elevates itself.", "denied");

        if (!policy.AdminDumpsEnabled)
            return new(false, $"Administrative dumps are disabled by machine policy ({policy.Source ?? "no policy file"}). " +
                              "An administrator must enable them (AdminDumpsEnabled).", "denied");

        if (policy.AllowedOperators is { } allowed
            && !allowed.Contains(context.OperatorIdentity, StringComparer.OrdinalIgnoreCase))
            return new(false, $"The operator '{context.OperatorIdentity}' is not on the allow-list of the administrative dump policy.", "denied");

        var reason = context.Admin.Reason?.Trim() ?? "";
        if (policy.RequireReason && reason.Length < policy.MinReasonLength)
            return new(false, $"A justification of at least {policy.MinReasonLength} characters is required (--reason).", "denied");

        return new(true, $"Administrative authorization granted to '{context.OperatorIdentity}' for process owned by " +
                         $"'{context.TargetOwner ?? "unknown"}'.", "admin");
    }
}
