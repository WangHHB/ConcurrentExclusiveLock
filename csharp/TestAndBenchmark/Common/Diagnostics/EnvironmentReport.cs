using System.Runtime;
using System.Runtime.InteropServices;

namespace TestAndBenchmark.Common.Diagnostics;

internal static class EnvironmentReport
{
    public static void Print()
    {
        Console.WriteLine("ConcurrentExclusivePack TestAndBenchmark");
        Console.WriteLine($"StartedAt          : {DateTimeOffset.Now:O}");
        Console.WriteLine($".NET               : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS                 : {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Architecture       : {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"LogicalProcessors  : {Environment.ProcessorCount}");
        Console.WriteLine($"ServerGC           : {GCSettings.IsServerGC}");
        Console.WriteLine($"GCLatencyMode      : {GCSettings.LatencyMode}");
        Console.WriteLine();
    }
}
