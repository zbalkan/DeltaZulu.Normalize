using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LogCluster.Cli;

internal sealed record LogRecord(long SequenceNumber, string Message, string? Source);

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

internal sealed class LogClusterInputTooLargeException(string message) : Exception(message);

internal sealed record MiningResult(int RecordCount, IReadOnlyList<CandidateOutput> Candidates, string Strategy);
internal sealed record CandidateOutput(int Support, double Specificity, string LogClusterPattern, string LiblognormRule, bool IsExecutableRule, IReadOnlyList<string> RuleWarnings, IReadOnlyList<GapOutput> Gaps, CandidateScore Score);
internal sealed record GapOutput(int MinWords, int MaxWords, int Observations, IReadOnlyList<string> Samples, string? SuggestedParser, double ParserConfidence);
internal sealed record CandidateScore(double Total, double Support, double AnchorQuality, double GapConsistency, double PatternSpecificity);

internal sealed class LogClusterMiner(LogClusterOptions options)
{
    // Reused for both strategies: "materialize" runs this once and caches the array; "stream"
    // re-invokes it (and re-tokenizes) once per pass, trading CPU for not holding every record
    // in memory at once. The two strategies differ only in whether tokenizedPass() below is
    // backed by a cached array or a fresh re-enumeration of recordSource().
    public MiningResult Mine(IEnumerable<LogRecord> records) => Mine(() => records, estimatedInputBytes: null);

    public MiningResult Mine(Func<IEnumerable<LogRecord>> recordSource, long? estimatedInputBytes)
    {
        var dictionary = new TokenDictionary();
        var streaming = ShouldStream(estimatedInputBytes, options);

        Func<IEnumerable<TokenizedRecord>> tokenizedPass;
        if (streaming)
        {
            tokenizedPass = () => TokenizedRecord.Stream(recordSource(), dictionary, options.MaxRecords, options.MaxInputBytes);
        }
        else
        {
            var materialized = TokenizedRecord.Stream(recordSource(), dictionary, options.MaxRecords, options.MaxInputBytes).ToArray();
            tokenizedPass = () => materialized;
        }

        var (frequentWords, recordCount) = DiscoverFrequentWords(tokenizedPass());
        var candidates = GenerateCandidates(tokenizedPass(), frequentWords);
        var routes = new Dictionary<CandidateKey, EvidenceRoute>();
        MergeShiftedCandidates(candidates, routes);
        MergeLowDiversityVariants(candidates, routes, options.WordWeightThreshold);
        var survivors = candidates.Values.Distinct().Where(c => c.Support >= options.MinSupport).ToArray();
        foreach (var candidate in survivors)
        {
            candidate.InitializeGaps(options.MaxSamplesPerGap);
        }

        CollectEvidence(tokenizedPass(), frequentWords, candidates, dictionary, routes);

        var outputs = survivors.Select(c => c.ToOutput(recordCount, dictionary, options))
            .OrderByDescending(c => c.Score.Total)
            .ThenByDescending(c => c.Support)
            .ThenByDescending(c => c.Specificity)
            .ThenBy(c => c.LogClusterPattern, StringComparer.Ordinal)
            .Take(options.MaxCandidates)
            .ToArray();
        return new MiningResult(recordCount, outputs, streaming ? "stream" : "materialize");
    }

    // Picks the strategy once, before mining starts. Below the safety margin, holding every
    // tokenized record in memory is simpler and faster than re-reading from disk three times;
    // above it, streaming trades CPU (re-tokenizing each pass) for a memory ceiling set by the
    // still-finite TokenDictionary/candidate structures rather than by recordCount record count.
    internal static bool ShouldStream(long? estimatedInputBytes, LogClusterOptions options)
    {
        if (options.ForceMaterialize is { } forced)
        {
            return !forced;
        }

        if (estimatedInputBytes is not { } bytes)
        {
            return false;
        }

        var headroom = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (headroom <= 0)
        {
            headroom = options.MaxInputBytes;
        }

        const double tokenizedOverheadFactor = 6.0; // int[] tokens + dictionary entries + candidate bookkeeping vs. raw bytes
        const double safetyMargin = 0.5;
        var estimatedMemoryUsage = bytes * tokenizedOverheadFactor;
        return estimatedMemoryUsage > headroom * safetyMargin;
    }

    private static void CollectEvidence(IEnumerable<TokenizedRecord> records, bool[] frequentWords, Dictionary<CandidateKey, PatternCandidate> candidates, TokenDictionary dictionary, Dictionary<CandidateKey, EvidenceRoute> routes)
    {
        foreach (var record in records)
        {
            var anchors = AnchorBuffer.From(record.Tokens, frequentWords);
            if (anchors.Length == 0)
            {
                continue;
            }

            var key = new CandidateKey(anchors);
            if (!candidates.TryGetValue(key, out var candidate) || !candidate.KeepEvidence)
            {
                continue;
            }

            var route = routes.TryGetValue(key, out var r) ? r : default;
            candidate.ObserveGaps(record.Tokens, record.Separators, frequentWords, dictionary, route.Leading, route.Trailing, route.AbsorbedPositions);
        }
    }

    private static Dictionary<CandidateKey, PatternCandidate> GenerateCandidates(IEnumerable<TokenizedRecord> records, bool[] frequentWords)
    {
        var candidates = new Dictionary<CandidateKey, PatternCandidate>();
        foreach (var record in records)
        {
            var anchors = AnchorBuffer.From(record.Tokens, frequentWords);
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

    // Word-weight-style join (mirrors LogClusterC's --wweight): candidates that differ in
    // exactly one anchor position, whose distinct values at that position recur often enough
    // relative to their combined support (per LogClusterOptions.WordWeightThreshold), are
    // reported as one candidate with that position demoted to a gap instead of fragmenting into
    // one candidate per value (e.g. per-source-IP variants of the same alert).
    //
    // Scoped to candidates that haven't already absorbed a MergeShiftedCandidates edge merge:
    // composing an internal-position removal with a prior edge absorption would require
    // reindexing the edge route, which isn't worth the correctness risk for this heuristic.
    private static void MergeLowDiversityVariants(Dictionary<CandidateKey, PatternCandidate> candidates, Dictionary<CandidateKey, EvidenceRoute> routes, double threshold)
    {
        var alreadyEdgeMerged = new HashSet<PatternCandidate>(routes.Keys.Select(k => candidates[k]));

        var groups = new Dictionary<(int Position, CandidateKey Template), List<PatternCandidate>>();
        foreach (var candidate in candidates.Values.Distinct())
        {
            if (candidate.AnchorCount == 0 || alreadyEdgeMerged.Contains(candidate) || candidates[candidate.Key] != candidate)
            {
                continue;
            }

            for (var position = 0; position < candidate.AnchorCount; position++)
            {
                var groupKey = (position, candidate.TemplateKey(position));
                if (!groups.TryGetValue(groupKey, out var members))
                {
                    groups[groupKey] = members = [];
                }
                members.Add(candidate);
            }
        }

        foreach (var group in groups.OrderBy(g => g.Key.Position).ThenBy(g => g.Key.Template.ToString(), StringComparer.Ordinal))
        {
            var position = group.Key.Position;
            var live = group.Value.Where(c => candidates[c.Key] == c).Distinct().ToArray();
            if (live.Length < 2)
            {
                continue;
            }

            var combinedSupport = live.Sum(c => c.Support);
            if (live.Length > threshold * combinedSupport)
            {
                continue;
            }

            var mergedAnchors = live[0].AnchorsWithout(position);
            var mergedKey = new CandidateKey(mergedAnchors);
            if (!candidates.TryGetValue(mergedKey, out var merged))
            {
                merged = new PatternCandidate(mergedKey, mergedAnchors);
            }

            foreach (var member in live)
            {
                if (member == merged)
                {
                    continue;
                }

                merged.AbsorbSupport(member);
                candidates[member.Key] = merged;
                routes[member.Key] = new EvidenceRoute(Leading: 0, Trailing: 0, AbsorbedPositions: new HashSet<int> { position });
            }
            candidates[mergedKey] = merged;
        }
    }

    // Aggregation-style merge (mirrors LogClusterC's --aggrsup): a candidate whose anchor
    // sequence is exactly one token longer than another candidate's, with the extra token at
    // either edge, is a positionally-shifted variant of the same underlying pattern rather than
    // a distinct one. Merge it into the shorter candidate, folding the extra anchor into the
    // adjacent boundary gap. Only single-token, single-edge differences are merged: an
    // already-merged key that some longer candidate reduces to by more than one token is left
    // as a separate candidate rather than chaining merges across a larger gap.
    private static void MergeShiftedCandidates(Dictionary<CandidateKey, PatternCandidate> candidates, Dictionary<CandidateKey, EvidenceRoute> routes)
    {
        var originals = candidates.Values.Distinct().OrderBy(c => c.AnchorCount).ToArray();
        foreach (var extended in originals)
        {
            if (extended.AnchorCount == 0 || candidates[extended.Key] != extended)
            {
                continue;
            }

            if (candidates.TryGetValue(extended.ReducedKey(dropFirst: true), out var leadingBase) && leadingBase != extended && leadingBase.AnchorCount == extended.AnchorCount - 1)
            {
                leadingBase.AbsorbSupport(extended);
                candidates[extended.Key] = leadingBase;
                routes[extended.Key] = new EvidenceRoute(Leading: 1, Trailing: 0, AbsorbedPositions: null);
            }
            else if (candidates.TryGetValue(extended.ReducedKey(dropFirst: false), out var trailingBase) && trailingBase != extended && trailingBase.AnchorCount == extended.AnchorCount - 1)
            {
                trailingBase.AbsorbSupport(extended);
                candidates[extended.Key] = trailingBase;
                routes[extended.Key] = new EvidenceRoute(Leading: 0, Trailing: 1, AbsorbedPositions: null);
            }
        }
    }

    private (bool[] Frequent, int RecordCount) DiscoverFrequentWords(IEnumerable<TokenizedRecord> records)
    {
        // Sized lazily rather than up front: in the streaming strategy the dictionary is still
        // growing as this same pass tokenizes records, so the final token universe isn't known
        // until the pass completes.
        var counts = new List<int>();
        var seenStamp = new List<int>();
        var stamp = 0;
        var recordCount = 0;
        foreach (var record in records)
        {
            recordCount++;
            stamp++;
            foreach (var token in record.Tokens)
            {
                while (seenStamp.Count <= token)
                {
                    seenStamp.Add(0);
                    counts.Add(0);
                }

                if (seenStamp[token] == stamp)
                {
                    continue;
                }

                seenStamp[token] = stamp;
                counts[token]++;
            }
        }

        var frequent = new bool[counts.Count];
        for (var token = 0; token < counts.Count; token++)
        {
            frequent[token] = counts[token] >= options.MinSupport;
        }
        return (frequent, recordCount);
    }
}

internal sealed class PatternCandidate(CandidateKey key, int[] anchors)
{
    private readonly List<GapStatistics> _gaps = [];
    private readonly List<SeparatorStats> _separators = [];
    private long _lastSequence;
    public int AnchorCount => anchors.Length;
    public bool KeepEvidence => _gaps.Count > 0;
    public CandidateKey Key { get; } = key;
    public int Support { get; private set; }

    public void AbsorbSupport(PatternCandidate other) => Support += other.Support;

    // The anchor sequence with one internal position wildcarded (see
    // LogClusterMiner.MergeLowDiversityVariants), used to group candidates that differ only at
    // that one position.
    public int[] AnchorsWithout(int position)
    {
        var result = new int[anchors.Length - 1];
        Array.Copy(anchors, 0, result, 0, position);
        Array.Copy(anchors, position + 1, result, position, anchors.Length - position - 1);
        return result;
    }

    public void InitializeGaps(int maxSamples)
    {
        var gapCount = anchors.Length + 1;
        for (var i = 0; i < gapCount; i++)
        {
            _gaps.Add(new GapStatistics(maxSamples));
            _separators.Add(new SeparatorStats());
        }
    }

    // separators is aligned with tokens: separators[i] is the whitespace immediately before
    // tokens[i], and separators[^1] is the trailing whitespace after the last token.
    // absorbedPositions (0-indexed in encounter order, before extraLeadingAnchors/
    // extraTrailingAnchors are applied) marks additional individual anchor occurrences that
    // should also fold into the adjacent gap, from MergeLowDiversityVariants.
    public void ObserveGaps(ReadOnlySpan<int> tokens, ReadOnlySpan<string> separators, ReadOnlySpan<bool> frequentWords, TokenDictionary dictionary, int extraLeadingAnchors = 0, int extraTrailingAnchors = 0, HashSet<int>? absorbedPositions = null)
    {
        var gapIndex = 0;
        var anchorsSeen = 0;
        // Every absorbed occurrence (edge or internal) still counts toward how many anchor hits
        // this record is expected to produce, so the trailing-edge check below doesn't misfire
        // once an internal position has already been absorbed.
        var totalAnchorsInRecord = anchors.Length + extraLeadingAnchors + extraTrailingAnchors + (absorbedPositions?.Count ?? 0); var gapWords = new List<int>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (frequentWords[token])
            {
                var position = anchorsSeen;
                anchorsSeen++;
                var isEdgeAbsorbed = position < extraLeadingAnchors || anchorsSeen > totalAnchorsInRecord - extraTrailingAnchors;
                var isPositionAbsorbed = absorbedPositions is not null && absorbedPositions.Contains(position);
                if (isEdgeAbsorbed || isPositionAbsorbed)
                {
                    gapWords.Add(token);
                    continue;
                }

                _gaps[gapIndex].Observe(gapWords, dictionary);
                _separators[gapIndex].Observe(separators[i]);
                gapIndex++;
                gapWords.Clear();
            }
            else
            {
                gapWords.Add(token);
            }
        }
        _gaps[gapIndex].Observe(gapWords, dictionary);
        _separators[gapIndex].Observe(separators[^1]);
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

    // Anchors dropped from either edge of a positionally-shifted variant that was merged into
    // this candidate (see LogClusterMiner.MergeShiftedCandidates) don't split a gap for this
    // candidate's own anchor sequence; fold them into the adjacent boundary gap's word range
    // instead of treating them as anchors.
    public CandidateKey ReducedKey(bool dropFirst) => new(dropFirst ? anchors.AsSpan(1) : anchors.AsSpan(0, anchors.Length - 1));

    public CandidateKey TemplateKey(int position) => new(AnchorsWithout(position));

    public CandidateOutput ToOutput(int recordCount, TokenDictionary dictionary, LogClusterOptions options)
    {
        var renderedGaps = _gaps.Select(g => g.ToOutput()).ToArray();
        var separators = _separators.Select(s => s.Modal()).ToArray();
        var score = CandidateScorer.Score(Support, recordCount, anchors.Length, renderedGaps, options.WeightSupport, options.WeightAnchor, options.WeightGapConsistency, options.WeightSpecificity);
        var specificity = anchors.Length / (double)Math.Max(1, anchors.Length + renderedGaps.Count(g => g.MaxWords > 0));
        return new CandidateOutput(
            Support,
            specificity,
            RenderLogCluster(anchors, renderedGaps, separators, dictionary),
            RenderRule(anchors, renderedGaps, separators, dictionary, out var isExecutableRule, out var ruleWarnings),
            isExecutableRule,
            ruleWarnings, renderedGaps,
            score);
    }

    private static void AddRuleGap(Action<string, int> append, int gapIndex, GapOutput gap, bool isTrailing, ref int field, List<string> warnings)
    {
        if (gap.MaxWords == 0)
        {
            return;
        }

        if (!isTrailing && (gap.MinWords == 0 || gap.MaxWords > 1 || string.IsNullOrEmpty(gap.SuggestedParser)))
        {
            append($"/* unresolved gap: {gap.MinWords}-{gap.MaxWords} words */", gapIndex); warnings.Add($"Internal gap {field} spans {gap.MinWords}-{gap.MaxWords} words and cannot be rendered as an executable liblognorm parser.");
            return;
        }

        var parser = gap.SuggestedParser ?? (gap.MaxWords == 1 ? LiblognormMotifs.Word : LiblognormMotifs.Rest);
        if (isTrailing && (gap.MinWords == 0 || gap.MaxWords > 1))
        {
            parser = LiblognormMotifs.Rest;
        }

        append($"%field{field++}:{parser}%", gapIndex);
    }

    private static string EscapeLiteral(string token) => token.Contains('%') || token.Contains(':')
        ? token.Replace("%", "%%", StringComparison.Ordinal).Replace(":", "\\x3a", StringComparison.Ordinal)
        : token;

    // separators[i] is the modal separator observed immediately before anchor i (or, for the
    // final entry, the modal trailing separator); reused as the join before whichever rendered
    // part (gap placeholder or anchor literal) falls at that boundary.
    private static string RenderLogCluster(int[] anchors, GapOutput[] gaps, string[] separators, TokenDictionary dictionary)
    {
        var builder = new StringBuilder();
        void Append(string text, int gapIndex)
        {
            if (builder.Length > 0)
            {
                builder.Append(separators[gapIndex]);
            }
            builder.Append(text);
        }

        for (var i = 0; i < anchors.Length; i++)
        {
            if (gaps[i].MaxWords > 0)
            {
                Append($"*{{{gaps[i].MinWords},{gaps[i].MaxWords}}}", i);
            }
            Append(dictionary[anchors[i]], i);
        }

        if (gaps[^1].MaxWords > 0)
        {
            Append($"*{{{gaps[^1].MinWords},{gaps[^1].MaxWords}}}", anchors.Length);
        }
        return builder.ToString();
    }

    private static string RenderRule(int[] anchors, GapOutput[] gaps, string[] separators, TokenDictionary dictionary, out bool isExecutable, out IReadOnlyList<string> warnings)
    {
        var builder = new StringBuilder();
        void Append(string text, int gapIndex)
        {
            if (builder.Length > 0)
            {
                builder.Append(separators[gapIndex]);
            }
            builder.Append(text);
        }

        var ruleWarnings = new List<string>();
        var field = 1;

        for (var i = 0; i < anchors.Length; i++)
        {
            AddRuleGap(Append, i, gaps[i], isTrailing: false, ref field, ruleWarnings);
            Append(EscapeLiteral(dictionary[anchors[i]]), i);
        }

        AddRuleGap(Append, anchors.Length, gaps[^1], isTrailing: true, ref field, ruleWarnings);
        isExecutable = ruleWarnings.Count == 0;
        warnings = ruleWarnings;
        return builder.ToString();
    }
}

// Tracks the whitespace actually observed at one anchor boundary across matching records, so
// rendering can reproduce a delimiter other than a single ASCII space (e.g. CSV/pipe-separated
// logs) instead of always rejoining with ' '.
internal sealed class SeparatorStats
{
    private readonly Dictionary<string, int> _votes = new(StringComparer.Ordinal);

    public string Modal() => _votes.Count == 0
        ? " "
        : _votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).First().Key;

    public void Observe(string separator) => _votes[separator] = _votes.GetValueOrDefault(separator) + 1;
}

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

// Separators is aligned with Tokens: Separators[i] is the whitespace run immediately before
// Tokens[i], and Separators[^1] (length Tokens.Length + 1) is the trailing whitespace after the
// last token. This lets rendering reproduce the delimiter actually observed at each anchor
// boundary instead of always rejoining with a single ASCII space.
internal sealed record TokenizedRecord(long SequenceNumber, int[] Tokens, string[] Separators)
{
    // Shared by both mining strategies: "materialize" calls ToArray() on this once and caches
    // the result; "stream" leaves it lazy and re-enumerates recordSource() through it once per
    // pass, so records are tokenized on the fly and never all held in memory at once.
    public static IEnumerable<TokenizedRecord> Stream(IEnumerable<LogRecord> records, TokenDictionary dictionary, long maxRecords, long maxInputBytes)
    {
        long recordCount = 0;
        long totalBytes = 0;
        foreach (var record in records)
        {
            recordCount++;
            totalBytes += record.Message.Length;
            if (recordCount > maxRecords)
            {
                throw new LogClusterInputTooLargeException($"input exceeds --max-records ({maxRecords}); use a smaller input or increase the limit");
            }
            if (totalBytes > maxInputBytes)
            {
                throw new LogClusterInputTooLargeException($"input exceeds --max-input-bytes ({maxInputBytes}); use a smaller input or increase the limit");
            }
            var (tokens, separators) = Tokenize(record.Message, dictionary);
            yield return new TokenizedRecord(record.SequenceNumber, tokens, separators);
        }
    }

    private static (int[] Tokens, string[] Separators) Tokenize(string message, TokenDictionary dictionary)
    {
        var tokens = new List<int>();
        var separators = new List<string>();
        var separatorStart = 0;
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
                separators.Add(message[separatorStart..start]);
                tokens.Add(dictionary.GetOrAdd(message, start, i - start));
                separatorStart = i;
                start = -1;
            }
        }
        separators.Add(message[separatorStart..]);
        return (tokens.ToArray(), separators.ToArray());
    }
}

// Which anchor occurrences (0-indexed in encounter order) a record matching this route's key
// should fold into the adjacent gap instead of treating as a real split point for whichever
// PatternCandidate it's been merged into. Leading/Trailing come from MergeShiftedCandidates
// (edge extensions); AbsorbedPositions comes from MergeLowDiversityVariants (one internal
// position wildcarded). The two are never combined on the same route (see
// MergeLowDiversityVariants' scoping comment).
internal readonly record struct EvidenceRoute(int Leading, int Trailing, HashSet<int>? AbsorbedPositions);

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
    // Weights default to 0.35/0.30/0.20/0.15 (support/anchor/gaps/specificity), a fixed split
    // with no published derivation; --weight-support/--weight-anchor/--weight-gaps/
    // --weight-specificity let callers retune the mix for log types dissimilar to whatever
    // informed those defaults, rather than silently trusting an unstated tuning.
    public static CandidateScore Score(int support, int recordCount, int anchorCount, IReadOnlyList<GapOutput> gaps, double weightSupport, double weightAnchor, double weightGapConsistency, double weightSpecificity)
    {
        var supportStrength = Math.Min(100, 100.0 * Math.Log(1 + support) / Math.Log(1 + recordCount));
        var anchorQuality = Math.Min(100, anchorCount * 20.0);
        var variableGaps = gaps.Where(g => g.MaxWords > 0).ToArray();
        var gapConsistency = variableGaps.Length == 0 ? 100 : variableGaps.Average(g => g.MinWords == g.MaxWords ? 100 : 60 + (40.0 * g.MinWords / Math.Max(1, g.MaxWords)));
        var specificity = 100.0 * anchorCount / Math.Max(1, anchorCount + variableGaps.Length);
        var total = (supportStrength * weightSupport) + (anchorQuality * weightAnchor) + (gapConsistency * weightGapConsistency) + (specificity * weightSpecificity);
        return new CandidateScore(total, supportStrength, anchorQuality, gapConsistency, specificity);
    }
}
