namespace Natter.Chat.DingTalk;

/// <summary>Shared diagnostic redaction for DingTalk status events (B-02a).</summary>
public static class DingTalkDiagnostics
{
    public static string? Redact(string? value)
        => value is null
            ? null
            : Natter.Application.Redaction.SecretRedaction.Apply(
                value,
                Natter.Application.Redaction.SecretRedactionOptions.Admin);
}
