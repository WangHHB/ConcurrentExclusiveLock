namespace TestAndBenchmark.Common.Testing;

internal sealed record TestCase(string Area, string Name, Func<CancellationToken, Task> Body)
{
    public static TestCase Sync(string area, string name, Action body)
    {
        return new TestCase(area, name, _ =>
        {
            body();
            return Task.CompletedTask;
        });
    }

    public static TestCase Async(string area, string name, Func<CancellationToken, Task> body)
    {
        return new TestCase(area, name, body);
    }
}
