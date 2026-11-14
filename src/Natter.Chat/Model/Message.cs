using System.Text.Json;
namespace Natter.Chat;

/// <summary>
/// One message. Immutable: edits and deletions are expressed by events, never by mutating
/// an existing instance. <see cref="Id"/> is unique inside <c>(source, conversation.id)</c>.
/// </summary>
public sealed record Message(
    string Id,
    Conversation Conversation,
    User Sender,
    Content Content,
    DateTimeOffset CreatedAt,
    string? ReplyTo = null,
    DateTimeOffset? EditedAt = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null)
{
    public MessageKey Key(Source source) => new(source, Conversation.Id, Id);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Id))
            errors.Add("message.id must not be empty.");

        if (Conversation is null)
            errors.Add("message.conversation must not be null.");
        else if (string.IsNullOrWhiteSpace(Conversation.Id))
            errors.Add("message.conversation.id must not be empty.");

        if (Sender is null)
            errors.Add("message.sender must not be null.");
        else if (string.IsNullOrWhiteSpace(Sender.Id))
            errors.Add("message.sender.id must not be empty.");

        if (Content is null)
            errors.Add("message.content must not be null.");
        else
            errors.AddRange(Content.Validate());

        return errors;
    }

    public void EnsureValid()
    {
        var errors = Validate();
        if (errors.Count > 0)
            throw new NlmpValidationException("Message violates the NLMP model.", errors);
    }

    public override string ToString()
        => $"Message {{ Id = {Id}, Conversation = {Conversation?.Id}, Sender = {Sender?.Id}, CreatedAt = {CreatedAt:O} }}";
}
