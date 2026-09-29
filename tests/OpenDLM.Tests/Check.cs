namespace OpenDLM.Tests;

/// <summary>Thrown when an assertion fails. Kept separate so the runner can distinguish it from a crash.</summary>
public sealed class TestFailureException : Exception
{
    public TestFailureException(string message) : base(message)
    {
    }
}

/// <summary>
/// Minimal assertions. A dependency-free harness was chosen deliberately: adding
/// xunit would pull NuGet packages into a repository that is meant to build and
/// run completely offline.
/// </summary>
public static class Check
{
    public static void True(bool condition, string what)
    {
        if (!condition)
        {
            throw new TestFailureException("Expected true: " + what);
        }
    }

    public static void False(bool condition, string what) => True(!condition, what);

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new TestFailureException($"{what}: expected <{expected}>, got <{actual}>");
        }
    }

    public static void NotNull(object? value, string what)
    {
        if (value is null)
        {
            throw new TestFailureException("Expected a value, got null: " + what);
        }
    }

    public static void BytesEqual(byte[] expected, byte[] actual, string what)
    {
        if (expected.Length != actual.Length)
        {
            throw new TestFailureException(
                $"{what}: length mismatch, expected {expected.Length} bytes but got {actual.Length}");
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index] != actual[index])
            {
                throw new TestFailureException(
                    $"{what}: first differing byte at offset {index} (0x{expected[index]:X2} vs 0x{actual[index]:X2})");
            }
        }
    }

    public static void Contains(string haystack, string needle, string what)
    {
        if (!haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            throw new TestFailureException($"{what}: '{needle}' was not found in '{haystack}'");
        }
    }

    /// <summary>Runs an assertion that is expected to throw.</summary>
    public static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new TestFailureException($"{what}: expected {typeof(TException).Name} but got {ex.GetType().Name}");
        }

        throw new TestFailureException($"{what}: expected {typeof(TException).Name} but nothing was thrown");
    }
}
