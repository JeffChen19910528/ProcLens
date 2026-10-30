using System.CommandLine;
using ProcLens.Cli.Hosting;
using ProcLens.Core.Dumps.Crypto;

namespace ProcLens.Cli.Commands.Dumps;

/// <summary>
/// Reads passwords without ever taking them from command-line arguments (which end up in process listings and
/// shell history): either from a named environment variable or from a hidden prompt.
/// </summary>
internal sealed class PasswordInput(ICliConsole console, string label = "password")
{
    public Option<string?> EnvironmentVariable { get; } = new($"--{label}-env")
    {
        Description = console.T("Name of an environment variable that holds the {0} (never pass the {0} itself as an argument).", label),
    };

    public Option<bool> Prompt { get; } = new($"--{label}-prompt")
    {
        Description = console.T("Ask for the {0} on the terminal (input is hidden).", label),
    };

    /// <returns>The secret, null if none was requested, or an error message in <paramref name="error"/>.</returns>
    public SecretText? Read(ParseResult parse, bool confirm, out string? error)
    {
        error = null;
        var variable = parse.GetValue(EnvironmentVariable);
        if (!string.IsNullOrEmpty(variable))
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(value))
            {
                error = console.T("The environment variable '{0}' is not set or is empty.", variable);
                return null;
            }
            return SecretText.From(value);
        }

        if (!parse.GetValue(Prompt)) return null;

        if (Console.IsInputRedirected)
        {
            error = console.T("A password prompt needs an interactive terminal; use the environment variable option instead.");
            return null;
        }

        var first = ReadHidden(console.T("Password: "));
        if (confirm && first != ReadHidden(console.T("Repeat password: ")))
        {
            error = console.T("The passwords do not match.");
            return null;
        }
        return SecretText.From(first);
    }

    private string ReadHidden(string prompt)
    {
        console.Error.Write(prompt);
        var chars = new List<char>();
        while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
            else if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        console.Error.WriteLine();
        return new string(chars.ToArray());
    }
}
