using ProcLens.Cli;
using ProcLens.Cli.Localization;
using ProcLens.Core.Dumps;

namespace ProcLens.Integration.Tests;

/// <summary>
/// Runs the real CLI in-process with its audit log, key directory and policy redirected into a private temp
/// directory, so tests never touch the developer's own audit trail or master key. Environment variables are
/// process-wide, which is why every class using this base shares the "CLI" collection and runs serially.
/// </summary>
public abstract class CliTestBase : IDisposable
{
    protected const string PasswordVariable = "PROCLENS_TEST_PASSWORD";

    private static readonly string[] Variables =
        [ProcLensPaths.AuditVariable, ProcLensPaths.KeyDirectoryVariable, ProcLensPaths.PolicyVariable, ProcLensPaths.ConfigVariable, LanguageSettings.EnvironmentVariable, PasswordVariable];

    private readonly Dictionary<string, string?> _saved = Variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);

    protected CliTestBase()
    {
        Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "proclens-cli-" + Guid.NewGuid().ToString("N"))).FullName;
        AuditFile = Path.Combine(Root, "audit", "audit.jsonl");
        PolicyFile = Path.Combine(Root, "policy.json"); // does not exist: administrative dumps stay disabled
        Environment.SetEnvironmentVariable(ProcLensPaths.AuditVariable, AuditFile);
        Environment.SetEnvironmentVariable(ProcLensPaths.KeyDirectoryVariable, Path.Combine(Root, "keys"));
        Environment.SetEnvironmentVariable(ProcLensPaths.PolicyVariable, PolicyFile);
        Environment.SetEnvironmentVariable(ProcLensPaths.ConfigVariable, ConfigFile); // does not exist: English unless a test says otherwise
        Environment.SetEnvironmentVariable(LanguageSettings.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable(PasswordVariable, null);
    }

    protected string Root { get; }
    protected string AuditFile { get; }
    protected string PolicyFile { get; }
    protected string ConfigFile => Path.Combine(Root, "config.json");

    protected static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApplication.RunAsync(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}
