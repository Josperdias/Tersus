using System.Collections;

namespace Tersus.Tests.Framework;

public static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new AssertionException(message ?? "Expected condition to be true.");
        }
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition)
        {
            throw new AssertionException(message ?? "Expected condition to be false.");
        }
    }

    public static void Fail(string message) => throw new AssertionException(message);

    public static void Skip(string reason) => throw new SkipException(reason);

    public static void SkipIf(bool condition, string reason)
    {
        if (condition)
        {
            throw new SkipException(reason);
        }
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{message ?? "Values differ."}\n  expected: {Show(expected)}\n  actual:   {Show(actual)}");
        }
    }

    public static void NotEqual<T>(T notExpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
        {
            throw new AssertionException($"{message ?? "Values should differ."}\n  both:     {Show(actual)}");
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? message = null)
    {
        T[] e = expected.ToArray();
        T[] a = actual.ToArray();
        if (!e.SequenceEqual(a))
        {
            throw new AssertionException(
                $"{message ?? "Sequences differ."}\n  expected: [{string.Join(", ", e.Select(x => Show(x)))}]\n  actual:   [{string.Join(", ", a.Select(x => Show(x)))}]");
        }
    }

    public static void Null(object? value, string? message = null)
    {
        if (value is not null)
        {
            throw new AssertionException($"{message ?? "Expected null."}\n  actual: {Show(value)}");
        }
    }

    public static T NotNull<T>(T? value, string? message = null)
        where T : class
    {
        if (value is null)
        {
            throw new AssertionException(message ?? "Expected a non-null value.");
        }

        return value;
    }

    public static void Contains(string haystack, string needle, string? message = null)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new AssertionException($"{message ?? "Text does not contain the expected fragment."}\n  needle:   {Show(needle)}\n  haystack: {Show(haystack)}");
        }
    }

    public static void Contains<T>(IEnumerable<T> items, T item, string? message = null)
    {
        if (!items.Contains(item))
        {
            throw new AssertionException($"{message ?? "Collection does not contain the item."}\n  item: {Show(item)}");
        }
    }

    public static void DoesNotContain<T>(IEnumerable<T> items, T item, string? message = null)
    {
        if (items.Contains(item))
        {
            throw new AssertionException($"{message ?? "Collection must not contain the item."}\n  item: {Show(item)}");
        }
    }

    public static void InRange(long value, long low, long high, string? message = null)
    {
        if (value < low || value > high)
        {
            throw new AssertionException($"{message ?? "Value out of range."} {value} not in [{low}, {high}]");
        }
    }

    public static TException Throws<TException>(Action action, string? message = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new AssertionException($"{message ?? "Wrong exception type."}\n  expected: {typeof(TException).Name}\n  actual:   {ex.GetType().Name}: {ex.Message}");
        }

        throw new AssertionException($"{message ?? "Expected an exception."} ({typeof(TException).Name})");
    }

    public static string Show(object? value) => value switch
    {
        null => "<null>",
        string s => "\"" + s + "\"",
        IEnumerable e and not string => "[" + string.Join(", ", e.Cast<object?>().Select(Show)) + "]",
        _ => value.ToString() ?? "<null>",
    };
}
