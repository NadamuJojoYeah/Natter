using System.Text.Json;
using Natter.Application.Chat;
using Natter.Chat;
using Xunit;
using ConversationKey = Natter.Application.Chat.ConversationKey;

namespace Natter.Framework.Tests;

public sealed class ChatCommandRouterTests
{
    [Fact]
    public async Task Remember_list_forget_and_reset_are_host_handled()
    {
        var clock = new TestClock();
        var memories = new InMemoryMemoryStore();
        var summaries = new InMemorySummaryStore();
        var conversations = new InMemoryConversationStore();
        var router = new ChatCommandRouter(memories, summaries, conversations, new ChatCommandFactory(clock));
        var source = Source.Create("discord", "discord", "bot");
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = new User("u1", "User");
        var key = new ConversationKey(source.Key, conversation.Id, null, null);
        await conversations.AppendTurnAsync(key,
            new StoredMessage("user", "old", null, DateTimeOffset.UtcNow),
            new StoredMessage("assistant", "old", null, DateTimeOffset.UtcNow));

        var remember = Assert.IsType<InteractionRespondCommand>(await router.TryHandleAsync(
            Event(source, conversation, user, "remember", new() { ["text"] = "likes tea" })));
        Assert.True(remember.Payload.Ephemeral);
        Assert.Contains("Remembered", remember.Payload.Content.Text);

        var memory = Assert.Single(await memories.SearchAsync(new MemoryQuery(source, conversation, user)));
        var listed = Assert.IsType<InteractionRespondCommand>(await router.TryHandleAsync(
            Event(source, conversation, user, "memory", new())));
        Assert.Contains(memory.MemoryId, listed.Payload.Content.Text);
        Assert.Contains("likes tea", listed.Payload.Content.Text);

        var forgotten = Assert.IsType<InteractionRespondCommand>(await router.TryHandleAsync(
            Event(source, conversation, user, "forget", new() { ["target"] = memory.MemoryId })));
        Assert.Contains("successful", forgotten.Payload.Content.Text);
        Assert.Empty(await memories.SearchAsync(new MemoryQuery(source, conversation, user)));

        await router.TryHandleAsync(Event(source, conversation, user, "reset", new()));
        Assert.Empty(await conversations.GetRecentAsync(key, 20, 24000));
    }

    [Fact]
    public async Task Ask_falls_through_to_ai_and_shared_scope_is_refused()
    {
        var clock = new TestClock();
        var router = new ChatCommandRouter(
            new InMemoryMemoryStore(),
            new InMemorySummaryStore(),
            new InMemoryConversationStore(),
            new ChatCommandFactory(clock));
        var source = Source.Create("discord", "discord", "bot");
        var conversation = new Conversation("c1", ConversationKind.Guild, GuildId: "g1");
        var user = new User("u1", "User");

        Assert.Null(await router.TryHandleAsync(Event(source, conversation, user, "ask", new() { ["prompt"] = "hello" })));
        var denied = Assert.IsType<InteractionRespondCommand>(await router.TryHandleAsync(
            Event(source, conversation, user, "remember", new() { ["text"] = "x", ["scope"] = "guild" })));
        Assert.Contains("administrator", denied.Payload.Content.Text);
    }

    [Fact]
    public async Task Remember_uses_the_profile_selected_memory_store()
    {
        var hostStore = new InMemoryMemoryStore();
        var selected = new InMemoryMemoryStore();
        var catalog = new ContextComponentCatalog();
        catalog.RegisterMemory("sample#file", selected);
        var router = new ChatCommandRouter(
            hostStore,
            new InMemorySummaryStore(),
            new InMemoryConversationStore(),
            new ChatCommandFactory(new TestClock()),
            catalog);
        var source = Source.Create("discord", "discord", "bot");
        var conversation = new Conversation("c", ConversationKind.Direct);
        var user = new User("u", "U");
        var profile = new RuntimeProfile
        {
            Id = "p",
            Memory = new ProfileMemory { Store = "sample#file" },
            Ai = new ProfileAi { Provider = "p#r" },
        };

        using (ProfileContext.Push(profile))
            await router.TryHandleAsync(Event(source, conversation, user, "remember", new() { ["text"] = "selected" }));

        Assert.Single(await selected.SearchAsync(new MemoryQuery(source, conversation, user)));
        Assert.Empty(await hostStore.SearchAsync(new MemoryQuery(source, conversation, user)));
    }

    private static InteractionCreatedEvent Event(
        Source source,
        Conversation conversation,
        User user,
        string command,
        Dictionary<string, string> options)
        => new()
        {
            Id = $"evt:{Guid.NewGuid():N}",
            Source = source,
            Sequence = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            Payload = new InteractionPayload(new Interaction(
                "i1",
                InteractionKind.Slash,
                user,
                conversation,
                DateTimeOffset.UtcNow,
                command,
                options.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value)),
                null,
                "reply-token")),
        };
}
