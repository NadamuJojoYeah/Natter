using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>One responder contribution. <see cref="Factory"/> resolves the responder lazily.</summary>
public sealed record ChatResponderRegistration(
    string Id,
    int Priority,
    string Owner,
    Func<IChatResponder> Factory);

/// <summary>Priority-ordered responder contributions from plugins and hosts.</summary>
public interface IChatResponderRegistry
{
    void Register(ChatResponderRegistration registration);

    bool Unregister(string owner, string id);

    bool TryGet(string providerRef, out ChatResponderRegistration? registration);

    IReadOnlyList<ChatResponderRegistration> Registrations { get; }
}

/// <summary>Thread-safe responder registry; duplicate <c>(owner, id)</c> entries are rejected.</summary>
public sealed class ChatResponderRegistry : IChatResponderRegistry
{
    private readonly List<ChatResponderRegistration> _registrations = [];
    private readonly Lock _gate = new();

    public bool TryGet(string providerRef, out ChatResponderRegistration? registration)
    {
        registration = null;
        if (string.IsNullOrWhiteSpace(providerRef))
            return false;

        var separator = providerRef.IndexOf('#');
        var owner = separator < 0 ? null : providerRef[..separator];
        var id = separator < 0 ? providerRef : providerRef[(separator + 1)..];

        lock (_gate)
        {
            registration = _registrations.FirstOrDefault(item =>
                string.Equals(item.Id, id, StringComparison.Ordinal)
                && (owner is null || string.Equals(item.Owner, owner, StringComparison.Ordinal)));
            return registration is not null;
        }
    }

    public IReadOnlyList<ChatResponderRegistration> Registrations
    {
        get
        {
            lock (_gate)
                return _registrations.ToArray();
        }
    }

    public void Register(ChatResponderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (string.IsNullOrWhiteSpace(registration.Id))
            throw new ArgumentException("Responder contribution id must not be empty.", nameof(registration));

        lock (_gate)
        {
            if (_registrations.Any(existing =>
                    string.Equals(existing.Owner, registration.Owner, StringComparison.Ordinal)
                    && string.Equals(existing.Id, registration.Id, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Responder contribution '{registration.Id}' from '{registration.Owner}' is already registered.");
            }

            _registrations.Add(registration);
        }
    }

    public bool Unregister(string owner, string id)
    {
        lock (_gate)
        {
            return _registrations.RemoveAll(existing =>
                string.Equals(existing.Owner, owner, StringComparison.Ordinal)
                && string.Equals(existing.Id, id, StringComparison.Ordinal)) > 0;
        }
    }
}

/// <summary>
/// Calls responders by ascending priority, then owner/id; the first non-null command wins.
/// A failing responder is isolated so other plugins can still respond.
/// </summary>
public sealed class CompositeChatResponder : IChatResponder
{
    private readonly IChatResponderRegistry _registry;

    public CompositeChatResponder(IChatResponderRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public async ValueTask<ChatCommand?> RespondAsync(
        ChatEvent chatEvent,
        CancellationToken cancellationToken = default)
    {
        var registrations = _registry.Registrations
            .OrderBy(registration => registration.Priority)
            .ThenBy(registration => registration.Owner, StringComparer.Ordinal)
            .ThenBy(registration => registration.Id, StringComparer.Ordinal)
            .ToArray();

        foreach (var registration in registrations)
        {
            try
            {
                var responder = registration.Factory();
                var command = await responder.RespondAsync(chatEvent, cancellationToken).ConfigureAwait(false);
                if (command is not null)
                    return command;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A broken responder must not prevent the remaining plugins from answering.
            }
        }

        return null;
    }
}
