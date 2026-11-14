using Natter.Chat;

namespace Natter.Infrastructure.Chat;

/// <summary>Thread-safe registry of chat upstreams keyed by the structured source.</summary>
public sealed class ChatUpstreamRegistry : IChatUpstreamRegistry
{
    private readonly Dictionary<Source, IChatUpstream> _upstreams = [];
    private readonly Lock _gate = new();

    /// <summary>Raised after a new upstream is registered (used to attach ingress subscriptions).</summary>
    public event Action<IChatUpstream>? UpstreamRegistered;

    public IReadOnlyList<IChatUpstream> Upstreams
    {
        get
        {
            lock (_gate)
                return _upstreams.Values.ToArray();
        }
    }

    public void Register(IChatUpstream upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);

        var source = upstream.Source ?? throw new ArgumentException("The upstream source must not be null.", nameof(upstream));
        if (string.IsNullOrWhiteSpace(source.Protocol)
            || string.IsNullOrWhiteSpace(source.Platform)
            || string.IsNullOrWhiteSpace(source.SelfId))
        {
            throw new ArgumentException(
                "The upstream source requires non-empty protocol, platform and selfId.",
                nameof(upstream));
        }

        lock (_gate)
            _upstreams[source] = upstream;

        UpstreamRegistered?.Invoke(upstream);
    }

    public bool TryGet(Source source, out IChatUpstream? upstream)
    {
        if (source is null)
        {
            upstream = null;
            return false;
        }

        lock (_gate)
            return _upstreams.TryGetValue(source, out upstream);
    }
}
