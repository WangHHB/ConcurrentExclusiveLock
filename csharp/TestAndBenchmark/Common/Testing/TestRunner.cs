using System.Diagnostics;

namespace TestAndBenchmark.Common.Testing;

internal static class TestRunner
{
    public static async Task<int> RunAsync(IReadOnlyCollection<TestCase> tests, TimeSpan perTestTimeout)
    {
        Console.WriteLine($"Running {tests.Count} correctness tests");
        Console.WriteLine($"Per-test timeout: {perTestTimeout}");
        Console.WriteLine();

        int passed = 0;
        int failed = 0;

        foreach (TestCase test in tests)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var cancellation = new CancellationTokenSource(perTestTimeout);
                await test.Body(cancellation.Token).WaitAsync(perTestTimeout);

                stopwatch.Stop();
                passed++;
                Console.WriteLine($"[PASS] {test.Area} :: {test.Name} ({stopwatch.ElapsedMilliseconds} ms)");
            }
            catch (TimeoutException ex)
            {
                stopwatch.Stop();
                failed++;
                Console.WriteLine($"[FAIL] {test.Area} :: {test.Name} ({stopwatch.ElapsedMilliseconds} ms)");
                Console.WriteLine($"       Timeout: {ex.Message}");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                failed++;
                Console.WriteLine($"[FAIL] {test.Area} :: {test.Name} ({stopwatch.ElapsedMilliseconds} ms)");
                Console.WriteLine($"       {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Result: {passed} passed, {failed} failed, {tests.Count} total");
        return failed == 0 ? 0 : 1;
    }
}
