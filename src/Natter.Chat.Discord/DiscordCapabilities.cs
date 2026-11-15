namespace Natter.Chat.Discord;

public static class DiscordCapabilities
{
    public static Capabilities Create(DiscordOptions options) => new()
    {
        ConversationKinds =
        [
            ConversationKind.Direct,
            ConversationKind.Group,
            ConversationKind.Guild,
            ConversationKind.Thread,
        ],
        MaxMessageLength = options.MaxMessageLength,
        Markdown = true,
        Attachments = false,
        Mentions = true,
        Reply = true,
        Edit = true,
        StreamEdit = false,
        Delete = true,
        Typing = options.Typing,
        Ephemeral = true,
        SlashCommands = options.SlashCommands,
        Buttons = false,
        Modals = false,
        MaxAttachmentBytes = options.MaxAttachmentBytes,
    };
}
