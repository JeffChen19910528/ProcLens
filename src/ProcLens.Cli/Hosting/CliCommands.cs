using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;

namespace ProcLens.Cli.Hosting;

/// <summary>One leaf command (for example <c>dump create</c>). It receives what it needs through its constructor.</summary>
public interface ICliCommand
{
    /// <summary>The command group it belongs to, such as <c>dump</c>. Groups are assembled by <see cref="CliCommands"/>.</summary>
    string Group { get; }

    Command Build();
}

public static class CliCommands
{
    /// <summary>Group name, English description, and the order groups appear in.</summary>
    private static readonly (string Name, string Description)[] Groups =
    [
        ("process", "Inspect running processes."),
        ("system", "System information and capabilities."),
        ("dump", "Create, protect and inspect process dumps (Windows only)."),
        ("audit", "Inspect the audit trail."),
        ("policy", "Inspect the machine policy."),
        ("report", "Generate diagnostic reports."),
        ("config", "Display and save settings."),
    ];

    /// <summary>Adds every command in <paramref name="commands"/> to the container.</summary>
    public static IServiceCollection AddCliCommands(this IServiceCollection services)
    {
        foreach (var type in typeof(CliCommands).Assembly.GetTypes()
                     .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ICliCommand).IsAssignableFrom(t)))
            services.AddSingleton(typeof(ICliCommand), type);
        return services;
    }

    public static RootCommand BuildRoot(IEnumerable<ICliCommand> commands, ICliConsole console)
    {
        var root = new RootCommand(console.T("ProcLens - cross-platform process diagnostics toolkit."));

        // Read before the tree is built (see LanguageSettings.Resolve); declared here so the parser accepts it everywhere.
        root.Options.Add(new Option<string?>("--lang")
        {
            Description = console.T("Display language: en or zh-TW (overrides PROCLENS_LANG and the saved setting)."),
            Recursive = true,
        });
        var byGroup = commands.ToLookup(c => c.Group);

        foreach (var (name, description) in Groups)
        {
            var group = new Command(name, console.T(description));
            foreach (var command in byGroup[name]) group.Subcommands.Add(command.Build());
            root.Subcommands.Add(group);
        }

        var unknown = byGroup.Select(g => g.Key).Except(Groups.Select(g => g.Name)).ToList();
        if (unknown.Count > 0) throw new InvalidOperationException($"Commands belong to unknown group(s): {string.Join(", ", unknown)}.");
        return root;
    }
}
