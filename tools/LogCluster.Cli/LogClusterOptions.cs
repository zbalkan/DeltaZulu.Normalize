using System.Globalization;

namespace LogCluster.Cli;

internal sealed record LogClusterOptions
{
    public int MinSupport { get; init; } = 2;
    public int MaxCandidates { get; init; } = 50;
    public int MaxSamplesPerGap { get; init; } = 8;
    public bool Json { get; init; }
    public bool Verbose { get; init; }
    public bool SkipEmpty { get; init; } = true;
    public bool ShowHelp { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public List<string> Inputs { get; } = [];

    public static LogClusterOptions Parse(string[] args)
    {
        var options = new LogClusterOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help": return options with { ShowHelp = true };
                case "-m" or "--message":
                    if (++i >= args.Length)
                    {
                        return options with { Error = $"{args[i - 1]} requires a value" };
                    }

                    options = options with { Message = args[i] };
                    break;

                case "-s" or "--min-support":
                    if (++i >= args.Length || !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var minSupport) || minSupport < 1)
                    {
                        return options with { Error = "minimum support must be a positive integer" };
                    }

                    options = options with { MinSupport = minSupport };
                    break;

                case "-n" or "--max-candidates":
                    if (++i >= args.Length || !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var maxCandidates) || maxCandidates < 1)
                    {
                        return options with { Error = "maximum candidates must be a positive integer" };
                    }

                    options = options with { MaxCandidates = maxCandidates };
                    break;

                case "--max-samples":
                    if (++i >= args.Length || !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var maxSamples) || maxSamples < 1)
                    {
                        return options with { Error = "maximum samples must be a positive integer" };
                    }

                    options = options with { MaxSamplesPerGap = maxSamples };
                    break;

                case "--json": options = options with { Json = true }; break;
                case "-v" or "--verbose": options = options with { Verbose = true }; break;
                case "--keep-empty": options = options with { SkipEmpty = false }; break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        return options with { Error = $"unknown option: {args[i]}" };
                    }

                    options.Inputs.Add(args[i]);
                    break;
            }
        }
        return options;
    }

    public static void PrintUsage() => Console.Error.WriteLine("""
        usage: logcluster [options] [file-or-directory ...]

        Discovers recurring log message structures and suggests candidate liblognorm rules.
        Without file arguments, messages are read one per line from stdin.

          -s, --min-support <n>     minimum records that must contain a word or candidate (default: 2)
          -n, --max-candidates <n>  maximum candidates to print (default: 50)
              --max-samples <n>     bounded samples to retain per variable gap (default: 8)
          -m, --message <text>      mine one message supplied on the command line
              --json                emit JSON instead of text
          -v, --verbose             print gap samples and parser confidence
              --keep-empty          include empty input lines
          -h, --help                show this help
        """);
}