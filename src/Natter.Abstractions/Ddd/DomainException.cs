namespace Natter.Abstractions.Ddd;

/// <summary>Base type for domain invariants violations.</summary>
public class DomainException : Exception
{
    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Argument guards raising <see cref="DomainException"/> instead of BCL exceptions.</summary>
public static class Guard
{
    public static T NotNull<T>(T? value, string parameterName)
        where T : class
        => value ?? throw new DomainException($"'{parameterName}' must not be null.");

    public static string NotNullOrWhiteSpace(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"'{parameterName}' must not be null or whitespace.");
        return value;
    }

    public static int Positive(int value, string parameterName)
    {
        if (value <= 0)
            throw new DomainException($"'{parameterName}' must be greater than zero.");
        return value;
    }

    public static int NotNegative(int value, string parameterName)
    {
        if (value < 0)
            throw new DomainException($"'{parameterName}' must not be negative.");
        return value;
    }

    public static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new DomainException(message);
    }
}
