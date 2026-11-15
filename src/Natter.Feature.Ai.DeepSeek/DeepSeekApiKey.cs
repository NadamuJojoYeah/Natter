namespace Natter.Feature.Ai.DeepSeek;

/// <summary>Secret wrapper; it never prints the key.</summary>
public sealed class DeepSeekApiKey
{
    private DeepSeekApiKey(string value) => Value = value;

    internal string Value { get; }

    public static DeepSeekApiKey From(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("DeepSeek API key must not be empty.", nameof(value));

        return new DeepSeekApiKey(value.Trim());
    }

    public override string ToString() => "***";
}
