using System.CommandLine;
using System.Text.Json;
using ProcLens.Cli.Hosting;
using ProcLens.Core.Audit;
using ProcLens.Infrastructure.Audit;

namespace ProcLens.Cli.Commands.Audit;

internal sealed class AuditShowCommand(FileAuditLog log, ICliConsole console) : ICliCommand
{
    public string Group => "audit";

    public Command Build()
    {
        var json = CommonOptions.Json(console);
        var last = new Option<int>("--last") { Description = console.T("Show only the most recent N records."), DefaultValueFactory = _ => 20 };
        var command = new Command("show", console.T("Show recent audit records.")) { last, json };

        command.SetAction(parse =>
        {
            var records = log.ReadLast(Math.Max(1, parse.GetValue(last)));
            if (parse.GetValue(json))
            {
                foreach (var r in records) console.Line(JsonSerializer.Serialize(r, AuditChain.Json));
                return ExitCodes.Success;
            }

            if (records.Count == 0)
            {
                console.Line(console.T("No audit records in {0}.", log.Path));
                return ExitCodes.Success;
            }
            foreach (var r in records)
            {
                var target = r.TargetPid is null ? "" : $" pid {r.TargetPid} ({r.TargetName})";
                console.Line($"{r.Timestamp:u}  {r.Outcome,-9}  {r.Operation,-13}  {r.Operator}{(r.OperatorElevated ? " [" + console.T("elevated") + "]" : "")}{target}");
                console.Line(r.Authorization.Allowed
                    ? "    " + console.T("authorization: allowed by {0}: {1}", r.Authorization.Policy, r.Authorization.Detail)
                    : "    " + console.T("authorization: not granted: {0}", r.Authorization.Detail));
                if (r.Location is not null) console.Line("    " + console.T("location: {0}", r.Location));
                if (r.Justification is not null) console.Line("    " + console.T("justification: {0}", r.Justification));
                if (r.ErrorDetails is not null) console.Line("    " + console.T("error: {0}", r.ErrorDetails));
            }
            return ExitCodes.Success;
        });
        return command;
    }
}

internal sealed class AuditVerifyCommand(FileAuditLog log, ICliConsole console) : ICliCommand
{
    public string Group => "audit";

    public Command Build()
    {
        var json = CommonOptions.Json(console);
        var anchor = new Option<string?>("--anchor")
        {
            Description = console.T("A head hash you saved earlier. Verification also fails if that record is missing, i.e. the log was rewritten or cut short."),
        };
        var command = new Command("verify", console.T("Check the audit log's hash chain for edits, insertions or deletions.")) { anchor, json };

        command.SetAction(parse =>
        {
            var result = log.Verify(parse.GetValue(anchor));
            if (parse.GetValue(json)) console.Line(JsonSerializer.Serialize(result, Display.Json));
            else if (result.Ok)
            {
                console.Line(console.T("Audit log intact: {0} record(s) in {1}.", result.Records, log.Path));
                if (result.HeadHash is not null)
                {
                    console.Line(console.T("Head hash: {0}", result.HeadHash));
                    console.Line(console.T("Store the head hash where the log's writer cannot change it (a ticket, another host); later: audit verify --anchor <hash>."));
                }
            }
            else if (result.BrokenAtLine is null) console.ErrorLine(console.T("Audit log FAILED the anchor check: {0}", console.Localize(result.Problem ?? "")));
            else console.ErrorLine(console.T("Audit log TAMPERED or damaged at line {0}: {1}", result.BrokenAtLine, console.Localize(result.Problem ?? "")));
            return result.Ok ? ExitCodes.Success : ExitCodes.Failed;
        });
        return command;
    }
}
