namespace Natter.Application.Chat;

/// <summary>Registry view of one tool: identity, policy capabilities and P5.2 metadata.</summary>
public sealed record ToolDescriptor(
    string Name,
    string Description,
    ToolCapabilities Capabilities,
    ToolMetadata Metadata,
    ITool Tool)
{
    public IReadOnlyList<string> Permissions => Metadata.Permissions ?? [];

    public bool Supports(ToolCallProtocol protocol)
        => protocol == ToolCallProtocol.Text || Metadata.NativeCallSupported;
}

/// <summary>Result of provider/registry protocol negotiation.</summary>
public sealed record ToolProtocolNegotiation(
    bool Ok,
    ToolCallProtocol Protocol,
    string Code,
    string Reason)
{
    public static ToolProtocolNegotiation Success(ToolCallProtocol protocol, string reason)
        => new(true, protocol, ToolErrorCodes.Succeeded, reason);

    public static ToolProtocolNegotiation Unsupported(string reason)
        => new(false, ToolCallProtocol.Text, ToolErrorCodes.ProtocolUnsupported, reason);
}

/// <summary>
/// Tool catalog used by the task runtime: deduplicated descriptors, policy/permission filtering and
/// provider capability negotiation (native function calling vs the text fallback protocol).
/// </summary>
public interface IToolRegistry
{
    IReadOnlyList<ToolDescriptor> Descriptors { get; }

    /// <summary>Names that were registered more than once; the first registration wins.</summary>
    IReadOnlyList<string> DuplicateNames { get; }

    /// <summary>What the routed provider supports; used by <see cref="ToolProtocolNegotiator"/>.</summary>
    ToolProtocolCapabilities ProtocolCapabilities { get; }

    bool TryGet(string? name, out ToolDescriptor descriptor);

    IReadOnlyList<ToolDescriptor> Allowed(
        RuntimeProfile profile,
        IToolPolicy policy,
        IReadOnlySet<string>? grantedPermissions = null);

    bool HasPermissions(ToolDescriptor descriptor, IReadOnlySet<string>? grantedPermissions);

    ToolProtocolNegotiation Negotiate(ToolCallProtocol? preferred = null);
}

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ToolDescriptor> _byName;
    private readonly IReadOnlyList<ToolDescriptor> _descriptors;

    public ToolRegistry(
        IEnumerable<ITool>? tools = null,
        ToolProtocolCapabilities? protocolCapabilities = null)
    {
        ProtocolCapabilities = protocolCapabilities ?? ToolProtocolCapabilities.TextOnly;

        var byName = new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal);
        var duplicates = new List<string>();

        foreach (var tool in tools ?? [])
        {
            if (tool is null)
                continue;

            var name = (tool.Name ?? string.Empty).Trim();
            if (name.Length == 0)
                continue;

            if (byName.ContainsKey(name))
            {
                duplicates.Add(name);
                continue;
            }

            byName[name] = new ToolDescriptor(
                name,
                tool.Description ?? string.Empty,
                tool.Capabilities,
                tool.Metadata ?? ToolMetadata.Legacy,
                tool);
        }

        _byName = byName;
        _descriptors = byName.Values
            .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
            .ToArray();
        DuplicateNames = duplicates.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<ToolDescriptor> Descriptors => _descriptors;

    public IReadOnlyList<string> DuplicateNames { get; }

    public ToolProtocolCapabilities ProtocolCapabilities { get; }

    public bool TryGet(string? name, out ToolDescriptor descriptor)
    {
        descriptor = null!;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return _byName.TryGetValue(name.Trim(), out descriptor!);
    }

    public IReadOnlyList<ToolDescriptor> Allowed(
        RuntimeProfile profile,
        IToolPolicy policy,
        IReadOnlySet<string>? grantedPermissions = null)
        => _descriptors
            .Where(descriptor => policy.IsAllowed(descriptor.Tool, profile))
            .Where(descriptor => HasPermissions(descriptor, grantedPermissions))
            .ToArray();

    public bool HasPermissions(ToolDescriptor descriptor, IReadOnlySet<string>? grantedPermissions)
    {
        // Null means "the host registered this tool with its own trust decision" (legacy behaviour).
        if (grantedPermissions is null)
            return true;

        foreach (var permission in descriptor.Permissions)
        {
            if (!grantedPermissions.Contains(permission))
                return false;
        }

        return true;
    }

    public ToolProtocolNegotiation Negotiate(ToolCallProtocol? preferred = null)
        => ToolProtocolNegotiator.Negotiate(ProtocolCapabilities, _descriptors, preferred);
}

/// <summary>Chooses native function calling when both provider and every tool support it.</summary>
public static class ToolProtocolNegotiator
{
    public static ToolProtocolNegotiation Negotiate(
        ToolProtocolCapabilities provider,
        IReadOnlyList<ToolDescriptor> descriptors,
        ToolCallProtocol? preferred = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var nativeCompatible = descriptors.All(descriptor => descriptor.Metadata.NativeCallSupported);
        var nativeAvailable = provider.NativeFunctionCalling && nativeCompatible;
        var textAvailable = provider.TextProtocol;
        var preferNative = preferred is null or ToolCallProtocol.Native;

        if (nativeAvailable && (preferNative || !textAvailable))
        {
            return ToolProtocolNegotiation.Success(
                ToolCallProtocol.Native,
                "Provider and all tools support native tool calling.");
        }

        if (textAvailable)
        {
            return ToolProtocolNegotiation.Success(
                ToolCallProtocol.Text,
                nativeCompatible
                    ? "Text protocol selected."
                    : "One or more tools are not native-capable; using the text protocol.");
        }

        return ToolProtocolNegotiation.Unsupported(
            provider.NativeFunctionCalling && !nativeCompatible
                ? "The provider only supports native tool calls but the registry contains text-only tools."
                : "The provider supports neither native tool calls nor the text protocol.");
    }
}

/// <summary>Projects a registry descriptor into the P5.1 worker contract.</summary>
public static class ToolDescriptorExtensions
{
    public static TaskToolDescriptor ToTaskToolDescriptor(this ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new TaskToolDescriptor(descriptor.Name, descriptor.Description);
    }
}
