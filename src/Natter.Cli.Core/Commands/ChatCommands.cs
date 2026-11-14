using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Natter.Chat;
using Natter.Chat.Satori;
using Natter.Cli.Abstractions;
using Natter.Cli.Core.Rendering;

namespace Natter.Cli.Core.Commands;

/// <summary>Lists registered NLMP upstreams, connection state and negotiated capabilities.</summary>
public sealed class UpstreamsCommand : ICliCommandHandler
{
    private readonly IServiceProvider _services;

    public UpstreamsCommand(IServiceProvider services) => _services = services;

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var registry = _services.GetService<IChatUpstreamRegistry>();
        if (registry is null)
        {
            invocation.Console.Error.WriteLine("The NLMP chat module is not composed in this host.");
            return CliExitCodes.Failure;
        }

        var filter = invocation.GetOption("source");
        var rows = new List<UpstreamRow>();

        foreach (var upstream in registry.Upstreams.OrderBy(upstream => upstream.Source.Key, StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(filter) && !Matches(upstream.Source, filter))
                continue;

            var capabilities = await upstream.GetCapabilitiesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            rows.Add(new UpstreamRow(upstream.Source, upstream.IsConnected, capabilities));
        }

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonOutput.Serialize(rows));
            return CliExitCodes.Success;
        }

        if (rows.Count == 0)
        {
            invocation.Console.Out.WriteLine("No NLMP chat upstreams registered.");
            return CliExitCodes.Success;
        }

        ConsoleTable.Write(
            invocation.Console.Out,
            ["SOURCE", "PLATFORM", "SELF", "STATE", "KINDS", "CAPABILITIES"],
            rows.Select(row => (IReadOnlyList<string>)
            [
                row.Source.Protocol,
                row.Source.Platform,
                row.Source.SelfId,
                row.Connected ? "ready" : "disconnected",
                row.Capabilities.ConversationKinds.Count == 0
                    ? "-"
                    : string.Join(",", row.Capabilities.ConversationKinds).ToLowerInvariant(),
                Describe(row.Capabilities),
            ]).ToList());

        return CliExitCodes.Success;
    }

    private static bool Matches(Source source, string filter)
        => source.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || source.Protocol.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || source.Platform.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || source.SelfId.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string Describe(Capabilities capabilities)
    {
        var flags = new List<string>();
        Add(flags, capabilities.Markdown, "markdown");
        Add(flags, capabilities.Attachments, "attachments");
        Add(flags, capabilities.Mentions, "mentions");
        Add(flags, capabilities.Reply, "reply");
        Add(flags, capabilities.Edit, "edit");
        Add(flags, capabilities.StreamEdit, "streamEdit");
        Add(flags, capabilities.Delete, "delete");
        Add(flags, capabilities.Typing, "typing");
        Add(flags, capabilities.Reactions, "reactions");
        Add(flags, capabilities.Ephemeral, "ephemeral");
        Add(flags, capabilities.SlashCommands, "slash");
        Add(flags, capabilities.Buttons, "buttons");
        Add(flags, capabilities.Modals, "modals");

        if (capabilities.MaxMessageLength is { } maxLength)
            flags.Add($"max={maxLength}");

        if (capabilities.MaxAttachmentBytes is { } maxBytes)
            flags.Add($"attachments<={maxBytes}");

        return flags.Count == 0 ? "-" : string.Join(", ", flags);
    }

    private static void Add(List<string> flags, bool supported, string name)
    {
        if (supported)
            flags.Add(name);
    }

    private sealed record UpstreamRow(
        [property: JsonConverter(typeof(SourceProjectionConverter))] Source Source,
        bool Connected,
        Capabilities Capabilities);
}

/// <summary>Projects the structured source without the display-only key.</summary>
internal sealed class SourceProjectionConverter : JsonConverter<Source>
{
    public override Source Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        => System.Text.Json.JsonSerializer.Deserialize<Source>(ref reader, options)!;

    public override void Write(System.Text.Json.Utf8JsonWriter writer, Source value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("protocol", value.Protocol);
        writer.WriteString("platform", value.Platform);
        writer.WriteString("selfId", value.SelfId);
        writer.WriteString("key", value.Key);
        writer.WriteEndObject();
    }
}

/// <summary>Shows how Satori <c>login.features</c> map to NLMP capabilities and API routes.</summary>
public sealed class SatoriStatusCommand : ICliCommandHandler
{
    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var featureText = invocation.GetOption("features") ?? string.Empty;
        var features = featureText
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var snapshot = SatoriStatus.Describe(features);

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonOutput.Serialize(snapshot));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        var output = invocation.Console.Out;
        output.WriteLine($"Satori adapter — protocol {snapshot.ProtocolId}, NLMP {snapshot.NlmpVersion}");
        output.WriteLine(new string('-', 52));
        output.WriteLine($"features       : {(snapshot.Features.Count == 0 ? "(none supplied)" : string.Join(", ", snapshot.Features))}");
        output.WriteLine($"conversations  : {(snapshot.Capabilities.ConversationKinds.Count == 0 ? "-" : string.Join(", ", snapshot.Capabilities.ConversationKinds))}");
        output.WriteLine($"markdown       : {snapshot.Capabilities.Markdown.ToString().ToLowerInvariant()}");
        output.WriteLine($"edit/stream    : {snapshot.Capabilities.Edit.ToString().ToLowerInvariant()} / {snapshot.Capabilities.StreamEdit.ToString().ToLowerInvariant()}");
        output.WriteLine($"delete         : {snapshot.Capabilities.Delete.ToString().ToLowerInvariant()}");
        output.WriteLine($"attachments    : {snapshot.Capabilities.Attachments.ToString().ToLowerInvariant()}");
        output.WriteLine($"buttons/slash  : {snapshot.Capabilities.Buttons.ToString().ToLowerInvariant()} / {snapshot.Capabilities.SlashCommands.ToString().ToLowerInvariant()}");
        output.WriteLine($"max length     : {(snapshot.Capabilities.MaxMessageLength?.ToString() ?? "unknown (adapter chunks)")}");
        output.WriteLine();

        ConsoleTable.Write(
            output,
            ["NLMP COMMAND", "SATORI ROUTE", "SUPPORTED"],
            SatoriApiContract.CommandRoutes
                .Select(pair => (IReadOnlyList<string>)
                [
                    pair.Key,
                    pair.Value,
                    snapshot.SupportedCommandTypes.Contains(pair.Key) ? "yes" : "no",
                ])
                .Concat(snapshot.UnsupportedCommandTypes.Select(command => (IReadOnlyList<string>)
                [
                    command,
                    "-",
                    "no",
                ]))
                .ToList());

        output.WriteLine();
        output.WriteLine("Notes: markdown=false unless the adapter converts; streamEdit is an adapter policy;");
        output.WriteLine("       unknown features are ignored and missing capabilities are never assumed.");

        return ValueTask.FromResult(CliExitCodes.Success);
    }
}
