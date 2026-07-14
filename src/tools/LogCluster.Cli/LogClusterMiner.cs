using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LogCluster.Cli;

internal sealed record LogRecord(long SequenceNumber, string Message, string? Source);
internal sealed record MiningResult(int RecordCount, IReadOnlyList<CandidateOutput> Candidates);
internal sealed record CandidateOutput(
    int Support,
    double Specificity,
    string LogClusterPattern,
    string LiblognormRule,
    IReadOnlyList<GapOutput> Gaps,
    CandidateScore Score,
    bool IsExecutableRule,
    IReadOnlyList<string> RuleWarnings);
internal sealed record GapOutput(int MinWords, int MaxWords, int Observations, IReadOnlyList<string> Samples, string? SuggestedParser, double ParserConfidence);
internal sealed record CandidateScore(double Total, double Support, double AnchorQuality, double GapConsistency, double PatternSpecificity);

internal static class AnchorBuffer
{
    public static int[] From(ReadOnlySpan<int> tokens, ReadOnlySpan<bool> frequentWords)
    {
        var count = 0;
        foreach (var token in tokens)
        {
            if (frequentWords[token])
            {
                count++;
            }
        }
        if (count == 0)
        {
            return [];
        }

        var anchors = new int[count];
        var index = 0;
        foreach (var token in tokens)
        {
            if (frequentWords[token])
            {
                anchors[index++] = token;
            }
        }
        return anchors;
    }
}

internal static partial class LiblognormMotifs
{
    public const string DateIso = "date-iso";
    public const string Float = "float";
    public const string Ipv4 = "ipv4";
    public const string Ipv6 = "ipv6";
    public const string Mac48 = "mac48";
    public const string Number = "number";
    public const string Rest = "rest";
    public const string Word = "word";
    private static readonly Regex FloatRegex = FloatPattern();
    private static readonly Regex IntegerRegex = IntegerPattern();
    private static readonly Regex Ipv4Regex = Ipv4Pattern();
    private static readonly Regex Mac48Regex = Mac48Pattern();
    private static readonly Regex WordRegex = WordPattern();

    public static int Priority(string parser) => parser switch {
        Ipv4 or Ipv6 or Mac48 => 0,
        DateIso => 1,
        Number or Float => 2,
        Word => 10,
        Rest => 20,
        _ => 30,
    };

    public static IEnumerable<string> Recognize(string sample)
    {
        if (Ipv4Regex.IsMatch(sample) && IPAddress.TryParse(sample, out var ipv4) && ipv4.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            yield return Ipv4;
        }

        if (sample.Contains(':', StringComparison.Ordinal) && IPAddress.TryParse(sample, out var ipv6) && ipv6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            yield return Ipv6;
        }

        if (Mac48Regex.IsMatch(sample))
        {
            yield return Mac48;
        }

        if (IntegerRegex.IsMatch(sample))
        {
            yield return Number;
        }

        if (FloatRegex.IsMatch(sample))
        {
            yield return Float;
        }

        if (DateOnly.TryParseExact(sample, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            yield return DateIso;
        }

        if (WordRegex.IsMatch(sample))
        {
            yield return Word;
        }
    }

    [GeneratedRegex("^[+-]?(?:[0-9]+\\.[0-9]*|[0-9]*\\.[0-9]+)(?:[eE][+-]?[0-9]+)?$")]
    private static partial Regex FloatPattern();

    [GeneratedRegex("^[+-]?[0-9]+$")]
    private static partial Regex IntegerPattern();

    [GeneratedRegex("^(?:[0-9]{1,3}\\.){3}[0-9]{1,3}$")]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex("^[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}$")]
    private static partial Regex Mac48Pattern();

    [GeneratedRegex("^[^\\s]+$")]
    private static partial Regex WordPattern();
}

// Stateless on purpose: each mining pass re-tokenizes every record from the raw message
// instead of consulting a stored token array, so no pass needs to keep the whole corpus's
// tokens resident at once (see the ARCHITECTURE NOTE on LogClusterMiner). A per-record token
// array is still allocated here, but it is scoped to that single record's processing and is
// immediately collectible afterward — unlike a corpus-wide `TokenizedRecord[]` would be.
internal static class Tokenizer
{
    // Word boundaries collapse any run of whitespace (tabs, repeated spaces, ...) into a single split point;
    // the original separator width is discarded. RenderRule compensates with the `whitespace` motif, but
    // the human-readable LogClusterPattern is display-only and always rejoins with a single ASCII space.
    public static int[] Tokenize(string message, TokenDictionary dictionary)
    {
        var tokens = new List<int>();
        var start = -1;
        for (var i = 0; i <= message.Length; i++)
        {
            if (i < message.Length && !char.IsWhiteSpace(message[i]))
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else if (start >= 0)
            {
                tokens.Add(dictionary.GetOrAdd(message, start, i - start));
                start = -1;
            }
        }
        return tokens.ToArray();
    }
}

internal sealed class GapStatistics(int maxSamples)
{
    private readonly Dictionary<string, int> _parserVotes = new(StringComparer.Ordinal);
    private readonly List<string> _samples = new(maxSamples);
    public int MaxWords { get; private set; }
    public int MinWords { get; private set; } = int.MaxValue;
    public int Observations { get; private set; }

    public void Observe(IReadOnlyList<int> words, TokenDictionary dictionary)
    {
        Observations++;
        MinWords = Math.Min(MinWords, words.Count);
        MaxWords = Math.Max(MaxWords, words.Count);
        if (words.Count == 0)
        {
            return;
        }

        var sample = dictionary.Join(words);
        if (_samples.Count < maxSamples && !_samples.Contains(sample, StringComparer.Ordinal))
        {
            _samples.Add(sample);
        }
        foreach (var parser in LiblognormMotifs.Recognize(sample))
        {
            _parserVotes[parser] = _parserVotes.GetValueOrDefault(parser) + 1;
        }
    }

    public GapOutput ToOutput()
    {
        var min = MinWords == int.MaxValue ? 0 : MinWords;
        var suggestion = _parserVotes.OrderByDescending(v => v.Value).ThenBy(v => LiblognormMotifs.Priority(v.Key)).FirstOrDefault();
        var parser = suggestion.Key;
        var confidence = Observations == 0 || string.IsNullOrEmpty(parser) ? 0 : suggestion.Value / (double)Observations;
        if (MaxWords > 1 && confidence < 1.0)
        {
            parser = LiblognormMotifs.Rest;
        }

        return new GapOutput(min, MaxWords, Observations, _samples.ToArray(), string.IsNullOrEmpty(parser) ? null : parser, confidence);
    }
}

// ARCHITECTURE NOTE (do not "simplify" this back to a single buffered pass): mining needs
// three independent passes over the corpus (word frequency, candidate support, gap evidence),
// each depending on the previous one's output. The original logcluster.pl (Vaarandi) gets this
// for O(vocabulary + patterns) memory by re-opening and re-streaming the input file from disk
// for every pass (see find_frequent_words/find_candidates in logcluster.pl) instead of holding
// the whole tokenized corpus in RAM between passes. This class mirrors that: `recordSource` is
// invoked once per pass and must yield the identical records, in the identical order, every
// time it's called — the mutable state that persists across passes is only the shared
// TokenDictionary, the frequent-word table, and the candidate table, all of which are bounded
// by vocabulary/pattern cardinality rather than by input size. Do not replace the factory with
// a single materialized `IEnumerable<LogRecord>`/array reused across passes: that is exactly
// the full-corpus-retention design this was changed away from (it was flagged as an OOM risk
// for large log files, since MaxCandidates only trims the final output, not the working set).
internal sealed class LogClusterMiner(LogClusterOptions options)
{
    public MiningResult Mine(IEnumerable<LogRecord> records) => Mine(() => records);

    public MiningResult Mine(Func<IEnumerable<LogRecord>> recordSource)
    {
        var dictionary = new TokenDictionary();

        // Pass 1 (mirrors find_frequent_words): tokenize each record against the shared
        // dictionary and count, per token, how many distinct records contain it.
        var frequentWords = DiscoverFrequentWords(recordSource(), dictionary, out var recordCount);

        // Pass 2 (mirrors find_candidates' first half): re-tokenize fresh and group records by
        // their frequent-word anchor sequence, counting support per distinct sequence.
        var candidates = GenerateCandidates(recordSource(), dictionary, frequentWords);
        var survivors = candidates.Values.Where(c => c.Support >= options.MinSupport).ToArray();
        foreach (var candidate in survivors)
        {
            candidate.InitializeGaps(options.MaxSamplesPerGap);
        }

        // Pass 3 (mirrors find_candidates' Vars tracking): re-tokenize once more to collect
        // bounded gap evidence, but only for candidates that survived the support threshold.
        CollectEvidence(recordSource(), dictionary, frequentWords, candidates);

        var outputs = survivors.Select(c => c.ToOutput(recordCount, dictionary))
            .OrderByDescending(c => c.Score.Total)
            .ThenByDescending(c => c.Support)
            .ThenByDescending(c => c.Specificity)
            .ThenBy(c => c.LogClusterPattern, StringComparer.Ordinal)
            .Take(options.MaxCandidates)
            .ToArray();
        return new MiningResult(recordCount, outputs);
    }

    private static void CollectEvidence(IEnumerable<LogRecord> records, TokenDictionary dictionary, bool[] frequentWords, Dictionary<CandidateKey, PatternCandidate> candidates)
    {
        foreach (var record in records)
        {
            var tokens = Tokenizer.Tokenize(record.Message, dictionary);
            var anchors = AnchorBuffer.From(tokens, frequentWords);
            if (anchors.Length == 0)
            {
                continue;
            }

            if (!candidates.TryGetValue(new CandidateKey(anchors), out var candidate) || !candidate.KeepEvidence)
            {
                continue;
            }

            candidate.ObserveGaps(tokens, frequentWords, dictionary);
        }
    }

    private static Dictionary<CandidateKey, PatternCandidate> GenerateCandidates(IEnumerable<LogRecord> records, TokenDictionary dictionary, bool[] frequentWords)
    {
        var candidates = new Dictionary<CandidateKey, PatternCandidate>();
        foreach (var record in records)
        {
            var tokens = Tokenizer.Tokenize(record.Message, dictionary);
            var anchors = AnchorBuffer.From(tokens, frequentWords);
            if (anchors.Length == 0)
            {
                continue;
            }

            var key = new CandidateKey(anchors);
            if (!candidates.TryGetValue(key, out var candidate))
            {
                candidate = new PatternCandidate(key, anchors);
                candidates.Add(key, candidate);
            }
            candidate.ObserveSupport(record.SequenceNumber);
        }
        return candidates;
    }

    // The token dictionary is only complete once every record has been tokenized once, so the
    // per-token counters below cannot be pre-sized like the later passes' `bool[] frequentWords`
    // can — they grow alongside the dictionary as new words are discovered mid-pass.
    private bool[] DiscoverFrequentWords(IEnumerable<LogRecord> records, TokenDictionary dictionary, out int recordCount)
    {
        var counts = new List<int>();
        var seenStamp = new List<int>();
        var stamp = 0;
        var total = 0;
        foreach (var record in records)
        {
            total++;
            stamp++;
            foreach (var token in Tokenizer.Tokenize(record.Message, dictionary))
            {
                while (counts.Count <= token)
                {
                    counts.Add(0);
                    seenStamp.Add(0);
                }

                if (seenStamp[token] == stamp)
                {
                    continue;
                }

                seenStamp[token] = stamp;
                counts[token]++;
            }
        }

        recordCount = total;
        var frequent = new bool[dictionary.Count];
        for (var token = 0; token < counts.Count; token++)
        {
            frequent[token] = counts[token] >= options.MinSupport;
        }
        return frequent;
    }
}

internal sealed class PatternCandidate(CandidateKey key, int[] anchors)
{
    private readonly List<GapStatistics> _gaps = [];
    private long _lastSequence;
    public bool KeepEvidence => _gaps.Count > 0;
    public CandidateKey Key { get; } = key;
    public int Support { get; private set; }

    public void InitializeGaps(int maxSamples)
    {
        var gapCount = anchors.Length + 1;
        for (var i = 0; i < gapCount; i++)
        {
            _gaps.Add(new GapStatistics(maxSamples));
        }
    }

    public void ObserveGaps(ReadOnlySpan<int> tokens, ReadOnlySpan<bool> frequentWords, TokenDictionary dictionary)
    {
        var gapIndex = 0;
        var gapWords = new List<int>();
        foreach (var token in tokens)
        {
            if (frequentWords[token])
            {
                _gaps[gapIndex++].Observe(gapWords, dictionary);
                gapWords.Clear();
            }
            else
            {
                gapWords.Add(token);
            }
        }
        _gaps[gapIndex].Observe(gapWords, dictionary);
    }

    public void ObserveSupport(long sequenceNumber)
    {
        if (_lastSequence == sequenceNumber)
        {
            return;
        }

        _lastSequence = sequenceNumber;
        Support++;
    }

    public CandidateOutput ToOutput(int recordCount, TokenDictionary dictionary)
    {
        var renderedGaps = _gaps.Select(g => g.ToOutput()).ToArray();
        var score = CandidateScorer.Score(Support, recordCount, anchors.Length, renderedGaps);
        var specificity = anchors.Length / (double)Math.Max(1, anchors.Length + renderedGaps.Count(g => g.MaxWords > 0));
        var rule = RenderRule(anchors, renderedGaps, dictionary);
        return new CandidateOutput(
            Support,
            specificity,
            RenderLogCluster(anchors, renderedGaps, dictionary),
            rule.Text,
            renderedGaps,
            score,
            rule.IsExecutable,
            rule.Warnings);
    }

    private static void AddGap(List<string> parts, GapOutput gap)
    {
        if (gap.MaxWords > 0)
        {
            parts.Add($"*{{{gap.MinWords},{gap.MaxWords}}}");
        }
    }

    private static void AddRuleGap(List<string> parts, List<string> warnings, GapOutput gap, ref int field, bool isInternal)
    {
        if (gap.MaxWords == 0)
        {
            return;
        }

        var fieldName = $"field{field++}";
        if (isInternal && ((gap.MinWords == 0 && gap.MaxWords > 0) || (gap.MaxWords > 1 && gap.SuggestedParser == LiblognormMotifs.Rest)))
        {
            var sampleText = gap.Samples.Count == 0 ? string.Empty : $"; samples: {string.Join(" | ", gap.Samples)}";
            var warning = $"unresolved gap: {fieldName} spans {gap.MinWords}-{gap.MaxWords} words{sampleText}";
            warnings.Add(warning);
            parts.Add($"/* {warning} */");
            return;
        }

        // liblognorm field selectors are mandatory matches with no optional syntax; when MinWords == 0 the
        // gap may legitimately be absent, so a strict/single-word parser would reject those records. `rest`
        // is the closest best-effort approximation liblognorm offers for a possibly-empty span.
        var parser = gap.MinWords == 0
            ? LiblognormMotifs.Rest
            : gap.SuggestedParser ?? (gap.MaxWords == 1 ? LiblognormMotifs.Word : LiblognormMotifs.Rest);
        parts.Add($"%{fieldName}:{parser}%");
    }

    private static string EscapeLiteral(string token) => token.Contains('%') || token.Contains(':')
        ? token.Replace("%", "%%", StringComparison.Ordinal).Replace(":", "\\x3a", StringComparison.Ordinal)
        : token;

    private static string RenderLogCluster(int[] anchors, GapOutput[] gaps, TokenDictionary dictionary)
    {
        var parts = new List<string>(anchors.Length + gaps.Length);
        for (var i = 0; i < anchors.Length; i++)
        {
            AddGap(parts, gaps[i]);
            parts.Add(dictionary[anchors[i]]);
        }
        AddGap(parts, gaps[^1]);
        return string.Join(' ', parts);
    }

    private static RuleRenderResult RenderRule(int[] anchors, GapOutput[] gaps, TokenDictionary dictionary)
    {
        var parts = new List<string>(anchors.Length + gaps.Length);
        var warnings = new List<string>();
        var field = 1;
        for (var i = 0; i < anchors.Length; i++)
        {
            AddRuleGap(parts, warnings, gaps[i], ref field, isInternal: i > 0);
            parts.Add(EscapeLiteral(dictionary[anchors[i]]));
        }
        AddRuleGap(parts, warnings, gaps[^1], ref field, isInternal: false);
        // Tokenization only records whitespace as a boundary, not its exact run (tabs, repeated
        // spaces, ...); joining with a literal ASCII space would reject any record whose original
        // delimiter differs. `%-:whitespace%` is liblognorm's own motif for a variable-width,
        // unnamed whitespace run, so it reproduces the boundary faithfully instead of guessing.
        return new RuleRenderResult(string.Join("%-:whitespace%", parts), warnings.Count == 0, warnings.ToArray());
    }
}

internal sealed record RuleRenderResult(string Text, bool IsExecutable, IReadOnlyList<string> Warnings);

internal sealed class TokenDictionary
{
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly List<string> _tokens = [];

    public TokenDictionary()
    {
        _lookup = _ids.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public int Count => _tokens.Count;

    public string this[int id] => _tokens[id];

    public int GetOrAdd(string message, int start, int length)
    {
        var span = message.AsSpan(start, length);
        if (_lookup.TryGetValue(span, out var id))
        {
            return id;
        }

        var token = span.ToString();
        id = _tokens.Count;
        _ids.Add(token, id);
        _tokens.Add(token);
        return id;
    }

    public string Join(IReadOnlyList<int> tokenIds) => tokenIds.Count switch {
        0 => string.Empty,
        1 => _tokens[tokenIds[0]],
        _ => JoinMany(tokenIds),
    };

    private string JoinMany(IReadOnlyList<int> tokenIds)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < tokenIds.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(_tokens[tokenIds[i]]);
        }
        return builder.ToString();
    }
}

internal readonly record struct CandidateKey : IEquatable<CandidateKey>
{
    private readonly string _value;

    public CandidateKey(ReadOnlySpan<int> anchors)
    {
        if (anchors.Length == 0)
        {
            _value = string.Empty;
            return;
        }

        var builder = new StringBuilder(anchors.Length * 4);
        for (var i = 0; i < anchors.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('\u001f');
            }

            builder.Append(anchors[i]);
        }
        _value = builder.ToString();
    }

    public override string ToString() => _value ?? string.Empty;
}

internal static class CandidateScorer
{
    public static CandidateScore Score(int support, int recordCount, int anchorCount, IReadOnlyList<GapOutput> gaps)
    {
        var supportStrength = Math.Min(100, 100.0 * Math.Log(1 + support) / Math.Log(1 + recordCount));
        var anchorQuality = Math.Min(100, anchorCount * 20.0);
        var variableGaps = gaps.Where(g => g.MaxWords > 0).ToArray();
        var gapConsistency = variableGaps.Length == 0 ? 100 : variableGaps.Average(g => g.MinWords == g.MaxWords ? 100 : 60 + (40.0 * g.MinWords / Math.Max(1, g.MaxWords)));
        var specificity = 100.0 * anchorCount / Math.Max(1, anchorCount + variableGaps.Length);
        var total = (supportStrength * 0.35) + (anchorQuality * 0.30) + (gapConsistency * 0.20) + (specificity * 0.15);
        return new CandidateScore(total, supportStrength, anchorQuality, gapConsistency, specificity);
    }
}
