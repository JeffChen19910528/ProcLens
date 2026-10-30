using System.Text;
using ProcLens.Cli;
using ProcLens.Cli.Localization;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let monitoring stop cleanly
    cts.Cancel();
};

// Chinese text needs UTF-8 on the wire, whatever code page the console started with (cp950, cp437, a redirect).
// English output is left exactly as it always was. The console is put back afterwards.
var previousOutput = Console.OutputEncoding;
var switched = false;
try
{
    if (LanguageSettings.Resolve(args).Language == Languages.TraditionalChinese)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        switched = true;
    }
}
catch (IOException)
{
    // No console to configure (for example output is closed): carry on with whatever encoding it has.
}

try
{
    return await CliApplication.RunAsync(args, Console.Out, Console.Error, cts.Token);
}
finally
{
    if (switched)
    {
        try { Console.OutputEncoding = previousOutput; } catch (IOException) { }
    }
}
