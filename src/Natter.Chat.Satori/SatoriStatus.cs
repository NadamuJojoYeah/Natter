namespace Natter.Chat.Satori;

/// <summary>Diagnostic snapshot shown by <c>natter satori status</c>.</summary>
public sealed record SatoriStatusSnapshot(
    string ProtocolId,
    string NlmpVersion,
    IReadOnlyList<string> Features,
    Capabilities Capabilities,
    IReadOnlyList<string> SupportedCommandTypes,
    IReadOnlyList<string> UnsupportedCommandTypes);

/// <summary>Creates a status snapshot without connecting to a Satori server.</summary>
public static class SatoriStatus
{
    public static SatoriStatusSnapshot Describe(
        IEnumerable<string>? features = null,
        SatoriCapabilityOptions? options = null)
    {
        var list = (features ?? [])
            .Where(feature => !string.IsNullOrWhiteSpace(feature))
            .Select(feature => feature.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(feature => feature, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SatoriStatusSnapshot(
            ProtocolId: "satori",
            NlmpVersion: Nlmp.Version,
            Features: list,
            Capabilities: SatoriCapabilityMapper.Map(list, options),
            SupportedCommandTypes:
            [
                ChatCommandTypes.MessageSend,
                ChatCommandTypes.MessageUpdate,
                ChatCommandTypes.MessageDelete,
            ],
            UnsupportedCommandTypes:
            [
                ChatCommandTypes.TypingStart,
                ChatCommandTypes.InteractionRespond,
            ]);
    }
}
