namespace Natter.Plugin.Abstractions;

/// <summary>Resolves secret values. Implementations must never log values.</summary>
public interface ISecretProvider
{
    bool TryGet(string name, out string? value);
}

/// <summary>
/// Reads secrets from environment variables: the exact name first, then the
/// <c>NATTER_SECRET_</c> prefix. Values never enter manifests or diagnostics.
/// </summary>
public sealed class EnvironmentSecretProvider : ISecretProvider
{
    private const string Prefix = "NATTER_SECRET_";

    public bool TryGet(string name, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value))
            return true;

        value = Environment.GetEnvironmentVariable(Prefix + name);
        return !string.IsNullOrEmpty(value);
    }
}

/// <summary>In-memory secret provider for tests and embedded hosts.</summary>
public sealed class InMemorySecretProvider : ISecretProvider
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public InMemorySecretProvider()
    {
    }

    public InMemorySecretProvider(IEnumerable<KeyValuePair<string, string>> secrets)
    {
        foreach (var (name, value) in secrets)
            Set(name, value);
    }

    public void Set(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Secret name must not be empty.", nameof(name));

        lock (_gate)
            _secrets[name] = value;
    }

    public bool Remove(string name)
    {
        lock (_gate)
            return _secrets.Remove(name);
    }

    public bool TryGet(string name, out string? value)
    {
        lock (_gate)
            return _secrets.TryGetValue(name, out value);
    }
}
