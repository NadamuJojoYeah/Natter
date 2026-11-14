namespace Natter.Chat;

/// <summary>Payload of <c>message.created</c> and <c>message.updated</c> (full snapshot).</summary>
public sealed record MessagePayload(Message Message);

/// <summary>Payload of <c>message.deleted</c>; deletions are tombstones, not mutations.</summary>
public sealed record MessageDeletedPayload(Conversation Conversation, string MessageId, DateTimeOffset DeletedAt);

/// <summary>Payload of <c>interaction.created</c>.</summary>
public sealed record InteractionPayload(Interaction Interaction);

/// <summary>Payload of <c>upstream.status</c>; never drives message handling.</summary>
public sealed record UpstreamStatusPayload(UpstreamState State, string? Detail = null);
