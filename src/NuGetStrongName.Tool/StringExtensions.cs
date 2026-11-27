namespace JIT.NuGetStrongName;

/// <summary>
///     Ordinal, case-insensitive string helpers.
/// </summary>
public static class StringExtensions
{
    /// <summary>
    ///     Returns true when both strings are equal using <see cref="StringComparison.OrdinalIgnoreCase" />;
    ///     two nulls are equal, null and non-null are not.
    /// </summary>
    public static bool EqualsIgnoreCase(this string? operand, string? checkAgainst)
    {
        return string.Equals(operand, checkAgainst, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns true when <paramref name="operand" /> starts with <paramref name="value" />
    ///     using <see cref="StringComparison.OrdinalIgnoreCase" />.
    /// </summary>
    public static bool StartsWithIgnoreCase(this string operand, string value)
    {
        return operand.StartsWith(value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns true when <paramref name="operand" /> ends with <paramref name="value" />
    ///     using <see cref="StringComparison.OrdinalIgnoreCase" />.
    /// </summary>
    public static bool EndsWithIgnoreCase(this string operand, string value)
    {
        return operand.EndsWith(value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns true when <paramref name="operand" /> contains <paramref name="value" />
    ///     using <see cref="StringComparison.OrdinalIgnoreCase" />.
    /// </summary>
    public static bool ContainsIgnoreCase(this string operand, string value)
    {
        return operand.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns the index of the first occurrence of <paramref name="value" /> at or after
    ///     <paramref name="startIndex" /> using <see cref="StringComparison.OrdinalIgnoreCase" />, or -1.
    /// </summary>
    public static int IndexOfIgnoreCase(this string operand, string value, int startIndex = 0)
    {
        return operand.IndexOf(value, startIndex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns the index of the last occurrence of <paramref name="value" />
    ///     using <see cref="StringComparison.OrdinalIgnoreCase" />, or -1.
    /// </summary>
    public static int LastIndexOfIgnoreCase(this string operand, string value)
    {
        return operand.LastIndexOf(value, StringComparison.OrdinalIgnoreCase);
    }
}
