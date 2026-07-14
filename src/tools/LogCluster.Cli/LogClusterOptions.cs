using System.Globalization;

namespace LogCluster.Cli;

internal sealed record LogClusterOptions
{
    public int MinSupport { get; init; } = 2;
    public int MaxCandidates { get; init; } = 50;
    public int MaxSamplesPerGap { get; init; } = 8;
    public long MaxRecords { get; init; } = 5_000_000;
    public long MaxInputBytes { get; init; } = 2_147_483_648;
    public bool? ForceMaterialize { get; init; }
    public double WeightSupport { get; init; } = 0.35;
    public double WeightAnchor { get; init; } = 0.30;
    public double WeightGapConsistency { get; init; } = 0.20;
    public double WeightSpecificity { get; init; } = 0.15;

    // Default of 0.5 merges a group of candidates differing at exactly one anchor position when
    // that position's distinct values recur at least twice each on average relative to the
    // group's combined support (distinctValues <= 0.5 * combinedSupport) -- e.g. a handful of
    // recurring source IPs, not a firehose of one-off values.
    public double WordWeightThreshold { get; init; } = 0.5;
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

                case "--max-records":
                    if (++i >= args.Length || !long.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var maxRecords) || maxRecords < 1)
                    {
                        return options with { Error = "maximum records must be a positive integer" };
                    }

                    options = options with { MaxRecords = maxRecords };
                    break;

                case "--max-input-bytes":
                    if (++i >= args.Length || !long.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var maxInputBytes) || maxInputBytes < 1)
                    {
                        return options with { Error = "maximum input bytes must be a positive integer" };
                    }

                    options = options with { MaxInputBytes = maxInputBytes };
                    break;

                case "--json": options = options with { Json = true }; break;
                case "-v" or "--verbose": options = options with { Verbose = true }; break;
                case "--keep-empty": options = options with { SkipEmpty = false }; break;
                case "--materialize": options = options with { ForceMaterialize = true }; break;
                case "--stream": options = options with { ForceMaterialize = false }; break;

                case "--weight-support":
                    if (++i >= args.Length || !double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var weightSupport) || weightSupport < 0)
                    {
                        return options with { Error = "support weight must be a non-negative number" };
                    }

                    options = options with { WeightSupport = weightSupport };
                    break;

                case "--weight-anchor":
                    if (++i >= args.Length || !double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var weightAnchor) || weightAnchor < 0)
                    {
                        return options with { Error = "anchor weight must be a non-negative number" };
                    }

                    options = options with { WeightAnchor = weightAnchor };
                    break;

                case "--weight-gaps":
                    if (++i >= args.Length || !double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var weightGaps) || weightGaps < 0)
                    {
                        return options with { Error = "gap consistency weight must be a non-negative number" };
                    }

                    options = options with { WeightGapConsistency = weightGaps };
                    break;

                case "--weight-specificity":
                    if (++i >= args.Length || !double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var weightSpecificity) || weightSpecificity < 0)
                    {
                        return options with { Error = "specificity weight must be a non-negative number" };
                    }

                    options = options with { WeightSpecificity = weightSpecificity };
                    break;
                case "--wweight-threshold":
                    if (++i >= args.Length || !double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var wweightThreshold) || wweightThreshold < 0)
                    {
                        return options with { Error = "word-weight threshold must be a non-negative number" };
                    }

                    options = options with { WordWeightThreshold = wweightThreshold };
                    break;
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
              --max-records <n>     abort if input exceeds this many records (default: 5000000)
              --max-input-bytes <n> abort if input exceeds this many bytes (default: 2147483648)
              --materialize         force loading all records into memory (skip the streaming heuristic)
              --stream              force the re-read-from-disk streaming strategy
              --weight-support <n>     score weight for support strength (default: 0.35)
              --weight-anchor <n>      score weight for anchor quality (default: 0.30)
              --weight-gaps <n>        score weight for gap consistency (default: 0.20)
              --weight-specificity <n> score weight for pattern specificity (default: 0.15)
              --wweight-threshold <n>  merge single-anchor variants when distinct values <=
                                       threshold * combined support (default: 0.5)
          -m, --message <text>      mine one message supplied on the command line
              --json                emit JSON instead of text
          -v, --verbose             print gap samples and parser confidence
              --keep-empty          include empty input lines
          -h, --help                show this help
        """);
}
