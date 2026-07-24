namespace TestAndBenchmark.Stress.RandomStateMachine;

internal sealed record RandomStressOptions(
    string Profile,
    int Seed,
    int Workers,
    int EntityCount,
    TimeSpan? Duration,
    int ProgressSeconds,
    int PipelinePercent,
    int Spin,
    int SegmentsMin,
    int SegmentsMax,
    int ExceptionPercent,
    int YieldPercent)
{
    public static RandomStressOptions Parse(string[] args)
    {
        string profile = GetString(args, "--profile", "quick").ToLowerInvariant();
        int logical = Environment.ProcessorCount;
        int seed = GetInt(args, "--seed", Environment.TickCount);

        int workers;
        int entities;
        TimeSpan? duration;
        int progressSeconds;
        int pipelinePercent;
        int spin;
        int segmentsMin;
        int segmentsMax;
        int exceptionPercent;
        int yieldPercent;

        if (profile == "forever")
        {
            workers = logical * 2;
            entities = 10000;
            duration = null;
            progressSeconds = 60;
            pipelinePercent = 95;
            spin = 64;
            segmentsMin = 2;
            segmentsMax = 16;
            exceptionPercent = 10;
            yieldPercent = 1;
        }
        else if (profile == "max")
        {
            workers = logical * 8;
            entities = 1;
            duration = null;
            progressSeconds = 60;
            pipelinePercent = 98;
            spin = 256;
            segmentsMin = 8;
            segmentsMax = 32;
            exceptionPercent = 20;
            yieldPercent = 10;
        }
        else if (profile == "endurance")
        {
            workers = logical * 2;
            entities = 10000;
            duration = null;
            progressSeconds = 60;
            pipelinePercent = 95;
            spin = 64;
            segmentsMin = 2;
            segmentsMax = 16;
            exceptionPercent = 10;
            yieldPercent = 1;
        }
        else if (profile == "standard")
        {
            workers = logical;
            entities = 1000;
            duration = TimeSpan.FromSeconds(30);
            progressSeconds = 10;
            pipelinePercent = 90;
            spin = 64;
            segmentsMin = 1;
            segmentsMax = 12;
            exceptionPercent = 10;
            yieldPercent = 1;
        }
        else if (profile == "quick")
        {
            workers = Math.Min(4, logical);
            entities = 128;
            duration = TimeSpan.FromSeconds(3);
            progressSeconds = 0;
            pipelinePercent = 85;
            spin = 32;
            segmentsMin = 1;
            segmentsMax = 8;
            exceptionPercent = 10;
            yieldPercent = 0;
        }
        else
        {
            throw new ArgumentException($"Unknown stress profile: {profile}");
        }

        double workerMultiplier = GetDouble(args, "--worker-multiplier", GetDouble(args, "--workerMultiplier", 0));
        if (workerMultiplier > 0)
        {
            workers = Math.Max(1, (int)Math.Ceiling(logical * workerMultiplier));
        }

        segmentsMin = Math.Max(1, GetInt(args, "--segments-min", GetInt(args, "--segmentsMin", segmentsMin)));
        segmentsMax = Math.Max(segmentsMin, GetInt(args, "--segments-max", GetInt(args, "--segmentsMax", segmentsMax)));

        return new RandomStressOptions(
            profile,
            seed,
            Math.Max(1, GetInt(args, "--workers", workers)),
            Math.Max(1, GetInt(args, "--lock-instances", GetInt(args, "--entities", entities))),
            ParseDuration(args, duration),
            Math.Max(0, GetInt(args, "--progress-seconds", GetInt(args, "--progressSeconds", progressSeconds))),
            Math.Clamp(GetInt(args, "--pipeline-percent", GetInt(args, "--pipelinePercent", pipelinePercent)), 0, 100),
            Math.Max(0, GetInt(args, "--spin", spin)),
            segmentsMin,
            segmentsMax,
            Math.Clamp(GetInt(args, "--exception-percent", GetInt(args, "--exceptionPercent", exceptionPercent)), 0, 100),
            Math.Clamp(GetInt(args, "--yield-percent", GetInt(args, "--yieldPercent", yieldPercent)), 0, 100));
    }

    public string ToReproductionCommand()
    {
        string duration = Duration is null ? "" : $" --duration-seconds {Duration.Value.TotalSeconds:R}";
        return $"TestAndBenchmark.exe stress --profile {Profile} --seed {Seed} --workers {Workers} --lock-instances {EntityCount}{duration} --pipeline-percent {PipelinePercent} --spin {Spin} --segments-min {SegmentsMin} --segments-max {SegmentsMax} --exception-percent {ExceptionPercent} --yield-percent {YieldPercent}";
    }

    private static TimeSpan? ParseDuration(string[] args, TimeSpan? defaultValue)
    {
        int index = Array.IndexOf(args, "--duration-seconds");
        if (index < 0)
        {
            index = Array.IndexOf(args, "--durationSeconds");
        }
        if (index < 0 || index + 1 >= args.Length)
        {
            return defaultValue;
        }

        double seconds = double.Parse(args[index + 1], System.Globalization.CultureInfo.InvariantCulture);
        if (seconds <= 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static string GetString(string[] args, string name, string defaultValue)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length)
        {
            return defaultValue;
        }

        return args[index + 1];
    }

    private static int GetInt(string[] args, string name, int defaultValue)
    {
        return int.Parse(GetString(args, name, defaultValue.ToString()));
    }

    private static double GetDouble(string[] args, string name, double defaultValue)
    {
        return double.Parse(GetString(args, name, defaultValue.ToString("R")), System.Globalization.CultureInfo.InvariantCulture);
    }
}
