namespace TestAndBenchmark.Suite;

internal sealed record SuiteOptions(
    string Profile,
    string Group,
    bool IncludeMicro,
    int Seed)
{
    public static SuiteOptions Parse(string[] args)
    {
        return new SuiteOptions(
            GetString(args, "--profile", "quick").ToLowerInvariant(),
            GetString(args, "--group", "smoke").ToLowerInvariant(),
            HasFlag(args, "--include-micro"),
            GetInt(args, "--seed", 123456));
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

    private static bool HasFlag(string[] args, string name)
    {
        return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
    }
}
