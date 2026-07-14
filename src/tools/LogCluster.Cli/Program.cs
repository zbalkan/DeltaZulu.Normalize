using System.Text.Json;

namespace LogCluster.Cli
{
    internal static class Program
    {
        private static readonly JsonSerializerOptions jsonOpts = new JsonSerializerOptions { WriteIndented = true };

        private static int Main(string[] args)
        {
            var options = LogClusterOptions.Parse(args);
            if (options.ShowHelp)
            {
                LogClusterOptions.PrintUsage();
                return 0;
            }
            if (options.Error is not null)
            {
                Console.Error.WriteLine($"error: {options.Error}");
                LogClusterOptions.PrintUsage();
                return 1;
            }

            string? spooledStdinPath = null;
            if (options.Message is null && options.Inputs.Count == 0)
            {
                spooledStdinPath = SpoolStdin();
                options.Inputs.Add(spooledStdinPath);
            }

            try
            {
                MiningResult result;
                try
                {
                    var estimatedBytes = EstimateInputBytes(options);
                    result = new LogClusterMiner(options).Mine(() => ReadRecords(options), estimatedBytes);
                }
                catch (LogClusterInputTooLargeException ex)
                {
                    Console.Error.WriteLine($"error: {ex.Message}");
                    return 1;
                }
                if (result.RecordCount == 0)
                {
                    Console.Error.WriteLine("error: no input messages were provided");
                    return 1;
                }
                if (options.Verbose)
                {
                    Console.Error.WriteLine($"info: mining strategy: {result.Strategy}");
                }
                if (options.Json)
                {
                    if (options.ShowOutliers)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new { result.Candidates, result.OutlierCount, result.OutlierSamples }, jsonOpts));
                    }
                    else
                    {
                        Console.WriteLine(JsonSerializer.Serialize(result.Candidates, jsonOpts));
                    }
                }
                else
                {
                    PrintText(result, options);
                }

                return 0;
            }
            finally
            {
                if (spooledStdinPath is not null)
                {
                    File.Delete(spooledStdinPath);
                }
            }

            static string SpoolStdin()
            {
                var path = Path.GetTempFileName();
                using var writer = new StreamWriter(path);
                string? line;
                while ((line = Console.In.ReadLine()) is not null)
                {
                    writer.WriteLine(line);
                }
                return path;
            }

            static long EstimateInputBytes(LogClusterOptions options)
            {
                if (options.Message is not null)
                {
                    return options.Message.Length;
                }

                long total = 0;
                foreach (var input in options.Inputs)
                {
                    if (Directory.Exists(input))
                    {
                        foreach (var file in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
                        {
                            total += new FileInfo(file).Length;
                        }
                    }
                    else if (File.Exists(input))
                    {
                        total += new FileInfo(input).Length;
                    }
                }
                return total;
            }

            static IEnumerable<LogRecord> ReadRecords(LogClusterOptions options)
            {
                var sequence = new SequenceCounter();
                if (options.Message is not null)
                {
                    yield return new LogRecord(sequence.Next(), options.Message, "argument");
                    yield break;
                }

                foreach (var input in options.Inputs)
                {
                    if (Directory.Exists(input))
                    {
                        foreach (var file in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                        {
                            foreach (var record in ReadFile(file, sequence, options.SkipEmpty))
                            {
                                yield return record;
                            }
                        }
                    }
                    else
                    {
                        foreach (var record in ReadFile(input, sequence, options.SkipEmpty))
                        {
                            yield return record;
                        }
                    }
                }
            }

            static IEnumerable<LogRecord> ReadFile(string path, SequenceCounter sequence, bool skipEmpty)
            {
                using var reader = File.OpenText(path);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.Length != 0 || !skipEmpty)
                    {
                        yield return new LogRecord(sequence.Next(), line, path);
                    }
                }
            }

            static void PrintText(MiningResult result, LogClusterOptions options)
            {
                Console.WriteLine($"LogCluster.NET candidates: {result.Candidates.Count} (records: {result.RecordCount}, minimum support: {options.MinSupport})");
                Console.WriteLine();

                foreach (var candidate in result.Candidates)
                {
                    Console.WriteLine($"Score {candidate.Score.Total:F1}  Support {candidate.Support}  Specificity {candidate.Specificity:F2}");
                    Console.WriteLine($"  LogCluster: {candidate.LogClusterPattern}");
                    Console.WriteLine($"  Rule:       {candidate.LiblognormRule}");
                    if (!candidate.IsExecutableRule)
                    {
                        Console.WriteLine("  Rule note:  structural sketch only; unresolved internal gaps make this non-executable as a liblognorm rule.");
                    }
                    Console.WriteLine($"  Score parts support={candidate.Score.Support:F1}, anchors={candidate.Score.AnchorQuality:F1}, gaps={candidate.Score.GapConsistency:F1}, specificity={candidate.Score.PatternSpecificity:F1}");
                    if (options.Verbose)
                    {
                        foreach (var warning in candidate.RuleWarnings)
                        {
                            Console.WriteLine($"  Warning: {warning}");
                        }
                        for (var i = 0; i < candidate.Gaps.Count; i++)
                        {
                            var gap = candidate.Gaps[i];
                            var parser = gap.SuggestedParser ?? LiblognormMotifs.Rest;
                            Console.WriteLine($"  Gap {i + 1}: words {gap.MinWords}-{gap.MaxWords}, observations {gap.Observations}, parser {parser} ({gap.ParserConfidence:P0})");
                            if (gap.Samples.Count > 0)
                            {
                                Console.WriteLine($"    samples: {string.Join(", ", gap.Samples)}");
                            }
                        }
                    }
                    Console.WriteLine();
                }

                if (options.ShowOutliers)
                {
                    Console.WriteLine($"Outliers: {result.OutlierCount} lines matched no surviving candidate");
                    foreach (var sample in result.OutlierSamples)
                    {
                        Console.WriteLine($"  {sample}");
                    }
                }
            }
        }
    }

    internal sealed class SequenceCounter
    {
        private long _value;

        public long Next() => ++_value;
    }
}
