using Microsoft.Extensions.DependencyInjection;
using Natter.Cli.Abstractions;

namespace Natter.Cli.Core.Commands;

/// <summary>Built-in command descriptors contributed by the CLI kernel itself.</summary>
internal static class BuiltInCommands
{
    private const string Origin = "framework:cli";

    private static readonly CliOptionDescriptor JsonOption =
        new("json", "Emit machine-readable JSON.");

    public static IReadOnlyList<CliCommandDescriptor> Create() =>
    [
        new CliCommandDescriptor
        {
            Name = "version",
            Description = "Show framework, runtime and RID information.",
            Aliases = ["ver"],
            Options = [JsonOption],
            Origin = Origin,
            HandlerFactory = services => services.GetRequiredService<VersionCommand>(),
        },
        new CliCommandDescriptor
        {
            Name = "doctor",
            Description = "Show the composed host: plugins, adapters, event bus, commands.",
            Options = [JsonOption],
            Origin = Origin,
            HandlerFactory = services => services.GetRequiredService<DoctorCommand>(),
        },
        new CliCommandDescriptor
        {
            Name = "ports",
            Description = "List port → adapter bindings (including plugin overrides).",
            Options =
            [
                JsonOption,
                new CliOptionDescriptor("port", "Filter by port or adapter name.", TakesValue: true),
            ],
            Origin = Origin,
            HandlerFactory = services => services.GetRequiredService<PortsCommand>(),
        },
        new CliCommandDescriptor
        {
            Name = "upstreams",
            Description = "List NLMP chat upstreams, connection state and capabilities.",
            Options =
            [
                new CliOptionDescriptor("source", "Filter by source key, protocol, platform or self id.", TakesValue: true),
                JsonOption,
            ],
            Origin = Origin,
            HandlerFactory = services => services.GetRequiredService<UpstreamsCommand>(),
        },
        new CliCommandDescriptor
        {
            Name = "satori status",
            Description = "Show Satori feature → NLMP capability mapping and API routes.",
            Options =
            [
                new CliOptionDescriptor("features", "Comma-separated Satori login.features to preview.", TakesValue: true),
                JsonOption,
            ],
            Origin = Origin,
            HandlerFactory = services => services.GetRequiredService<SatoriStatusCommand>(),
        },
        new CliCommandDescriptor
        {
            Name = "demo",
            Description = "Run the end-to-end DDD/Rx demo (aggregate → events → stream).",
            Options =
            [
                new CliOptionDescriptor("name", "Counter name.", TakesValue: true, DefaultValue: "demo"),
                new CliOptionDescriptor("amount", "Increment amount.", TakesValue: true, DefaultValue: "1"),
                new CliOptionDescriptor("times", "Number of increments.", TakesValue: true, DefaultValue: "3"),
                JsonOption,
            ],
            Origin = Origin,
            HandlerFactory = services => services.GetRequiredService<DemoCommand>(),
        },
        new CliCommandDescriptor
        {
            Name = "exec",
            Description = "Run multiple CLI commands inside one host (plugins/state are shared).",
            Arguments =
            [
                new CliArgumentDescriptor("command", "Command line to run (repeat the argument for more commands).", Required: true),
                new CliArgumentDescriptor("more", "Additional command lines."),
            ],
            Options = [new CliOptionDescriptor("keep-going", "Continue after a failing command.")],
            Origin = Origin,
            HandlerFactory = services => services.GetRequiredService<ExecCommand>(),
        },
        new CliCommandDescriptor
        {
            Name = "events list",
            Description = "Show buffered domain events from the journal.",
            Options =
            [
                new CliOptionDescriptor("limit", "Maximum number of events.", TakesValue: true, DefaultValue: "20"),
                JsonOption,
            ],
            Origin = Origin,
            HandlerFactory = services => services.GetRequiredService<EventsListCommand>(),
        },
        new CliCommandDescriptor
        {
            Name = "events watch",
            Description = "Subscribe to the Rx domain event stream for a few seconds.",
            Options =
            [
                new CliOptionDescriptor("seconds", "How long to listen.", TakesValue: true, DefaultValue: "3"),
            ],
            Origin = Origin,
            HandlerFactory = services => services.GetRequiredService<EventsWatchCommand>(),
        },
        new CliCommandDescriptor
        {
            Name = "events clear",
            Description = "Clear the in-memory event journal.",
            Origin = Origin,
            HandlerFactory = services => services.GetRequiredService<EventsClearCommand>(),
        },
        new CliCommandDescriptor
        {
            Name = "plugins list",
            Description = "List loaded/failed plugins.",
            Options =
            [
                new CliOptionDescriptor("failed", "Only show plugins that failed to load."),
                JsonOption,
            ],
            Origin = Origin,
            HandlerFactory = services => services.GetRequiredService<PluginsListCommand>(),
        },
        new CliCommandDescriptor
        {
            Name = "plugins inspect",
            Description = "Probe a plugin directory without loading it.",
            Arguments = [new CliArgumentDescriptor("directory", "Plugin directory to inspect.", Required: true)],
            Options = [JsonOption],
            Origin = Origin,
            HandlerFactory = services => services.GetRequiredService<PluginsInspectCommand>(),
        },
        new CliCommandDescriptor
        {
            Name = "help",
            Description = "Show help for the CLI or a specific command.",
            Arguments = [new CliArgumentDescriptor("command", "Command name (optional).")],
            Origin = Origin,
            HandlerFactory = services => services.GetRequiredService<HelpCommand>(),
        },
    ];
}
