namespace TestAndBenchmark.Common.Testing;

internal static class TestAssert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            Fail(message ?? "Expected condition to be true.");
        }
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition)
        {
            Fail(message ?? "Expected condition to be false.");
        }
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Fail(message ?? $"Expected <{expected}> but got <{actual}>.");
        }
    }

    public static void NotEqual<T>(T notExpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
        {
            Fail(message ?? $"Did not expect <{actual}>.");
        }
    }

    public static void InRange(int actual, int minInclusive, int maxInclusive, string? message = null)
    {
        if (actual < minInclusive || actual > maxInclusive)
        {
            Fail(message ?? $"Expected <{actual}> to be in range [{minInclusive}, {maxInclusive}].");
        }
    }

    public static void Throws<TException>(Action action, string? message = null)
        where TException : Exception
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
            Fail(message ?? $"Expected {typeof(TException).Name}, but got {ex.GetType().Name}: {ex.Message}");
        }

        Fail(message ?? $"Expected {typeof(TException).Name}, but no exception was thrown.");
    }

    public static void Eventually(Func<bool> predicate, TimeSpan timeout, string? message = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            Thread.Sleep(10);
        }

        Fail(message ?? $"Condition was not met within {timeout}.");
    }

    public static void Fail(string message)
    {
        throw new TestAssertionException(message);
    }
}
