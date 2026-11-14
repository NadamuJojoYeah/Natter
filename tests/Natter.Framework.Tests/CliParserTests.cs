using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class CliParserTests
{
    private static readonly CliCommandDescriptor DemoCommand = new()
    {
        Name = "demo",
        Options =
        [
            new CliOptionDescriptor("name", TakesValue: true, DefaultValue: "demo"),
            new CliOptionDescriptor("times", TakesValue: true, DefaultValue: "3"),
            new CliOptionDescriptor("json"),
        ],
        Arguments =
        [
            new CliArgumentDescriptor("counter", Required: true),
            new CliArgumentDescriptor("extra"),
        ],
        Origin = "test",
        HandlerFactory = _ => throw new NotSupportedException(),
    };

    [Fact]
    public void Parses_long_options_positionals_and_defaults()
    {
        var result = CliParser.Parse(DemoCommand, ["primary", "--name", "x", "--json"]);

        Assert.Null(result.Error);
        Assert.False(result.ShowHelp);
        Assert.Equal("x", result.Options["name"]);
        Assert.Equal("3", result.Options["times"]);
        Assert.True(result.Options.ContainsKey("json"));
        Assert.Equal(new[] { "primary" }, result.Positionals);
    }

    [Fact]
    public void Parses_equals_syntax_and_short_flags()
    {
        var result = CliParser.Parse(DemoCommand, ["primary", "--name=value with spaces", "-h"]);

        Assert.True(result.ShowHelp);
        Assert.Equal("value with spaces", result.Options["name"]);
    }

    [Fact]
    public void Reports_missing_required_argument()
    {
        var result = CliParser.Parse(DemoCommand, ["--name", "x"]);

        Assert.NotNull(result.Error);
        Assert.Contains("counter", result.Error);
    }

    [Fact]
    public void Reports_missing_option_value()
    {
        var result = CliParser.Parse(DemoCommand, ["primary", "--name"]);

        Assert.NotNull(result.Error);
        Assert.Contains("requires a value", result.Error);
    }

    [Fact]
    public void Stops_option_parsing_after_double_dash()
    {
        var result = CliParser.Parse(DemoCommand, ["primary", "--", "--not-an-option"]);

        Assert.Null(result.Error);
        Assert.Equal(new[] { "primary", "--not-an-option" }, result.Positionals);
    }

    [Fact]
    public void Tokenizer_honours_quotes_and_escapes()
    {
        var tokens = CliCommandTokenizer.Split("demo --name \"hello world\" --path 'C:\\temp'");

        Assert.Equal(new[] { "demo", "--name", "hello world", "--path", "C:\\temp" }, tokens);
    }

    [Fact]
    public void Registry_resolves_the_longest_command_path()
    {
        var registry = BuildRegistry(
            Descriptor("plugins"),
            Descriptor("plugins list"),
            Descriptor("plugins inspect"));

        Assert.True(registry.TryResolve(["plugins", "inspect", "path"], out var command, out var consumed));
        Assert.Equal("plugins inspect", command.Name);
        Assert.Equal(2, consumed);
    }

    [Fact]
    public void Registry_resolves_aliases()
    {
        var registry = BuildRegistry(new CliCommandDescriptor
        {
            Name = "version",
            Aliases = ["ver"],
            Origin = "test",
            HandlerFactory = _ => throw new NotSupportedException(),
        });

        Assert.True(registry.TryGet("ver", out var command));
        Assert.Equal("version", command.Name);
    }

    private static CliCommandDescriptor Descriptor(string name) => new()
    {
        Name = name,
        Origin = "test",
        HandlerFactory = _ => throw new NotSupportedException(),
    };

    private static CliCommandRegistry BuildRegistry(params CliCommandDescriptor[] descriptors)
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<CliCommandRegistry>.Instance;
        return new CliCommandRegistry(descriptors, [], logger);
    }
}
