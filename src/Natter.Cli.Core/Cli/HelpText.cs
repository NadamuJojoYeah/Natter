using System.Text;
using Natter.Cli.Abstractions;

namespace Natter.Cli.Core;

/// <summary>Renders usage/help text for the CLI kernel.</summary>
public static class HelpText
{
    public static string RenderGlobal(CliCommandRegistry registry)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Natter — hexagonal architecture + DDD framework");
        builder.AppendLine();
        builder.AppendLine("Usage:");
        builder.AppendLine("  natter <command> [arguments] [options]");
        builder.AppendLine("  natter help [command]");
        builder.AppendLine();
        builder.AppendLine("Commands:");

        var width = registry.Commands.Count == 0
            ? 10
            : Math.Min(28, registry.Commands.Max(command => command.Name.Length) + 2);

        foreach (var command in registry.Commands)
        {
            builder.Append("  ");
            builder.Append(command.Name.PadRight(width));
            builder.AppendLine(command.Description ?? string.Empty);
        }

        builder.AppendLine();
        builder.AppendLine("Global options:");
        builder.AppendLine("  --plugin-dir <path>   Additional plugin directory (repeatable).");
        builder.AppendLine("  --no-plugins          Do not scan plugin directories.");
        builder.AppendLine("  --verbose, -v         Enable verbose/debug logging.");
        builder.AppendLine("  --trace-events        Print every domain event published to the Rx stream.");
        builder.AppendLine("  -h, --help            Show help. Use 'natter help <command>' for command help.");
        return builder.ToString();
    }

    public static string RenderCommand(CliCommandDescriptor command)
    {
        var builder = new StringBuilder();
        var usage = command.Usage
                    ?? $"natter {command.Name}{RenderUsageSuffix(command)}";

        builder.AppendLine(usage);
        if (!string.IsNullOrWhiteSpace(command.Description))
            builder.AppendLine(command.Description);
        builder.AppendLine();

        if (command.Arguments.Count > 0)
        {
            builder.AppendLine("Arguments:");
            foreach (var argument in command.Arguments)
            {
                var name = argument.Required ? $"<{argument.Name}>" : $"[{argument.Name}]";
                builder.Append("  ");
                builder.Append(name.PadRight(24));
                builder.AppendLine(argument.Description ?? string.Empty);
            }

            builder.AppendLine();
        }

        if (command.Options.Count > 0)
        {
            builder.AppendLine("Options:");
            foreach (var option in command.Options)
            {
                var labels = new List<string>();
                if (option.ShortName is not null)
                    labels.Add($"-{option.ShortName}");
                labels.Add($"--{option.Name}");

                var label = string.Join(", ", labels);
                if (option.TakesValue)
                    label += " <value>";

                builder.Append("  ");
                builder.Append(label.PadRight(24));
                builder.Append(option.Description ?? string.Empty);
                if (option.DefaultValue is not null)
                    builder.Append($" (default: {option.DefaultValue})");
                builder.AppendLine();
            }

            builder.AppendLine();
        }

        builder.AppendLine($"Source: {command.Origin}");
        return builder.ToString();
    }

    private static string RenderUsageSuffix(CliCommandDescriptor command)
    {
        var suffix = new StringBuilder();
        foreach (var argument in command.Arguments)
            suffix.Append(argument.Required ? $" <{argument.Name}>" : $" [{argument.Name}]");

        if (command.Options.Count > 0)
            suffix.Append(" [options]");

        return suffix.ToString();
    }
}
