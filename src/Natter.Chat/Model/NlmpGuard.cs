namespace Natter.Chat;

/// <summary>Small guards that raise <see cref="NlmpValidationException"/> from model factories.</summary>
internal static class NlmpGuard
{
    public static string NotNullOrWhiteSpace(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new NlmpValidationException($"'{parameterName}' must not be null or whitespace.");

        return value;
    }

    public static T NotNull<T>(T? value, string parameterName)
        where T : class
        => value ?? throw new NlmpValidationException($"'{parameterName}' must not be null.");

    public static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new NlmpValidationException(message);
    }
}
