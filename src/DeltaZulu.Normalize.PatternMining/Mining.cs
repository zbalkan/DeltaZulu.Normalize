using System.Text;

namespace DeltaZulu.Normalize.PatternMining;

public sealed class CandidateScorer
{
    public CandidateScore Score(PatternCandidate candidate, IReadOnlyDictionary<int, long> anchorLineCounts, long totalRecords, long maximumSupport)
    {
        var supportStrength = maximumSupport <= 1 ? 1d : Math.Log(1d + candidate.RawSupport) / Math.Log(1d + maximumSupport);
        var anchorQuality = candidate.AnchorIds.Length == 0
            ? 0d
            : candidate.AnchorIds.Select(id => 1d - Math.Clamp(anchorLineCounts[id] / (double)Math.Max(1, totalRecords), 0d, 1d)).Average();
        var gapConsistency = candidate.Gaps.Length == 0 ? 1d : candidate.Gaps.Select(ScoreGapConsistency).Average();
        var averageVariableWords = candidate.Gaps.Length == 0
            ? 0d
            : candidate.Gaps.Average(g => g.LengthHistogram.Sum(x => x.Key * x.Value) / (double)Math.Max(1, g.ObservationCount));
        var specificity = candidate.AnchorIds.Length / Math.Max(1d, candidate.AnchorIds.Length + averageVariableWords);

        var support = 35d * supportStrength;
        var anchors = 30d * anchorQuality;
        var gaps = 20d * gapConsistency;
        var specific = 15d * specificity;
        return new CandidateScore(Math.Clamp(support + anchors + gaps + specific, 0d, 100d), support, anchors, gaps, specific);
    }

    private static double ScoreGapConsistency(GapStatistics gap)
    {
        if (gap.ObservationCount == 0)
            return 0d;
        if (gap.Range.Maximum == 0)
            return 1d;
        var dominantLength = gap.LengthHistogram.Values.Max() / (double)gap.ObservationCount;
        var spreadPenalty = 1d / (1d + gap.Range.Maximum - gap.Range.Minimum);
        var syntaxConsistency = gap.SyntaxMatches.Count == 0 || gap.Samples.Seen == 0
            ? 0.5d
            : Math.Clamp(gap.SyntaxMatches.Values.Max() / (double)gap.Samples.Seen, 0d, 1d);
        return Math.Clamp(0.45d * dominantLength + 0.30d * spreadPenalty + 0.25d * syntaxConsistency, 0d, 1d);
    }
}

internal sealed class TokenDictionary
{
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly List<string> _tokens = [];

    public int GetOrAdd(string token)
    {
        if (_ids.TryGetValue(token, out var id))
            return id;
        id = _tokens.Count;
        _ids.Add(token, id);
        _tokens.Add(token);
        return id;
    }

    public string Resolve(int id) => _tokens[id];
}

public sealed class LogClusterMiner
{
    private readonly ILogTokenizer _tokenizer;
    private readonly SyntaxRegistry _syntaxes;
    private readonly CandidateScorer _scorer;

    public LogClusterMiner(ILogTokenizer? tokenizer = null, SyntaxRegistry? syntaxes = null, CandidateScorer? scorer = null)
    {
        _tokenizer = tokenizer ?? new RegexLogTokenizer();
        _syntaxes = syntaxes ?? new SyntaxRegistry();
        _scorer = scorer ?? new CandidateScorer();
    }

    public async Task<MiningResult> MineAsync(IReplayableLogSource source, MiningOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new MiningOptions();
        ValidateOptions(options);

        var (totalRecords, exactWordCounts) = await CountWordsAsync(source, cancellationToken);
        if (totalRecords == 0)
            return new MiningResult(0, 0, new Dictionary<string, long>(), []);

        var support = ResolveSupport(options, totalRecords);
        var frequentWords = exactWordCounts.Where(x => x.Value >= support).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var tokens = new TokenDictionary();
        var frequentIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var anchorCounts = new Dictionary<int, long>();
        foreach (var (word, count) in frequentWords)
        {
            var id = tokens.GetOrAdd(word);
            frequentIds.Add(word, id);
            anchorCounts.Add(id, count);
        }

        var candidates = await GenerateCandidatesAsync(source, frequentIds, options, cancellationToken);
        foreach (var key in candidates.Where(x => x.Value.RawSupport < support).Select(x => x.Key).ToArray())
            candidates.Remove(key);
        if (candidates.Count == 0)
            return new MiningResult(totalRecords, support, frequentWords, []);

        await CollectEvidenceAsync(source, frequentIds, candidates, cancellationToken);
        var maximumSupport = candidates.Values.Max(x => x.RawSupport);
        foreach (var candidate in candidates.Values)
            candidate.Score = _scorer.Score(candidate, anchorCounts, totalRecords, maximumSupport);

        var renderer = new PatternRenderer(tokens, _syntaxes, options.SyntaxCoverageThreshold);
        var suggestions = candidates.Values
            .Where(x => x.Score.Total >= options.MinimumScore)
            .Select(x => new PatternSuggestion(x, renderer.RenderLogCluster(x), renderer.RenderLiblognorm(x)))
            .OrderByDescending(x => x.Candidate.Score.Total)
            .ThenByDescending(x => x.Candidate.RawSupport)
            .ThenByDescending(x => x.Candidate.AnchorIds.Length)
            .ThenBy(x => x.LogClusterPattern, StringComparer.Ordinal)
            .ToList();
        if (options.Top is > 0)
            suggestions = suggestions.Take(options.Top.Value).ToList();
        return new MiningResult(totalRecords, support, frequentWords, suggestions);
    }

    private async Task<(long Total, Dictionary<string, long> Counts)> CountWordsAsync(IReplayableLogSource source, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0;
        await foreach (var record in source.ReadPassAsync(cancellationToken))
        {
            total++;
            foreach (var word in new HashSet<string>(_tokenizer.Tokenize(record.Text), StringComparer.Ordinal))
            {
                counts.TryGetValue(word, out var count);
                counts[word] = count + 1;
            }
        }
        return (total, counts);
    }

    private async Task<Dictionary<CandidateKey, PatternCandidate>> GenerateCandidatesAsync(IReplayableLogSource source, IReadOnlyDictionary<string, int> frequentIds, MiningOptions options, CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<CandidateKey, PatternCandidate>();
        await foreach (var record in source.ReadPassAsync(cancellationToken))
        {
            var observation = BuildObservation(_tokenizer.Tokenize(record.Text), frequentIds);
            if (observation is null)
                continue;
            var (key, anchors, gaps, _) = observation.Value;
            if (!candidates.TryGetValue(key, out var candidate))
            {
                var seed = key.GetHashCode();
                candidate = new PatternCandidate
                {
                    Key = key,
                    AnchorIds = anchors,
                    Gaps = gaps.Select((length, index) => new GapStatistics(length, options.SampleSizePerGap, HashCode.Combine(seed, index))).ToArray(),
                    RawSupport = 1,
                    Examples = new ReservoirSampler<string>(options.ExampleCount, seed)
                };
                candidates.Add(key, candidate);
            }
            else
            {
                candidate.RawSupport++;
                for (var i = 0; i < gaps.Length; i++)
                    candidate.Gaps[i].ObserveLength(gaps[i]);
            }
        }
        return candidates;
    }

    private async Task CollectEvidenceAsync(IReplayableLogSource source, IReadOnlyDictionary<string, int> frequentIds, IReadOnlyDictionary<CandidateKey, PatternCandidate> candidates, CancellationToken cancellationToken)
    {
        await foreach (var record in source.ReadPassAsync(cancellationToken))
        {
            var observation = BuildObservation(_tokenizer.Tokenize(record.Text), frequentIds);
            if (observation is null)
                continue;
            var (key, _, _, values) = observation.Value;
            if (!candidates.TryGetValue(key, out var candidate))
                continue;
            candidate.Examples.Add(record.Text);
            for (var i = 0; i < candidate.Gaps.Length; i++)
            {
                var syntaxes = candidate.Gaps[i].Range is { Minimum: 1, Maximum: 1 } ? _syntaxes.Match(values[i]) : [];
                candidate.Gaps[i].ObserveSample(values[i], syntaxes);
            }
        }
    }

    private static (CandidateKey Key, int[] Anchors, int[] Gaps, string[] Values)? BuildObservation(IReadOnlyList<string> words, IReadOnlyDictionary<string, int> frequentIds)
    {
        var anchors = new List<int>();
        var gaps = new List<int>();
        var values = new List<string>();
        var current = new List<string>();
        foreach (var word in words)
        {
            if (frequentIds.TryGetValue(word, out var id))
            {
                anchors.Add(id);
                gaps.Add(current.Count);
                values.Add(string.Join(' ', current));
                current.Clear();
            }
            else
            {
                current.Add(word);
            }
        }
        if (anchors.Count == 0)
            return null;
        gaps.Add(current.Count);
        values.Add(string.Join(' ', current));
        var ids = anchors.ToArray();
        return (new CandidateKey(ids), ids, gaps.ToArray(), values.ToArray());
    }

    private static long ResolveSupport(MiningOptions options, long total) =>
        options.Support is > 0 ? options.Support.Value : Math.Max(1, (long)Math.Floor(total * options.RelativeSupportPercent / 100d));

    private static void ValidateOptions(MiningOptions options)
    {
        if (options.Support is <= 0) throw new ArgumentOutOfRangeException(nameof(options.Support));
        if (options.RelativeSupportPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(options.RelativeSupportPercent));
        if (options.SampleSizePerGap < 0) throw new ArgumentOutOfRangeException(nameof(options.SampleSizePerGap));
        if (options.ExampleCount < 0) throw new ArgumentOutOfRangeException(nameof(options.ExampleCount));
        if (options.MinimumScore is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(options.MinimumScore));
        if (options.SyntaxCoverageThreshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(options.SyntaxCoverageThreshold));
    }
}

internal sealed class PatternRenderer(TokenDictionary tokens, SyntaxRegistry syntaxes, double threshold)
{
    public string RenderLogCluster(PatternCandidate candidate) => Render(candidate, false);
    public string RenderLiblognorm(PatternCandidate candidate) => Render(candidate, true);

    private string Render(PatternCandidate candidate, bool typed)
    {
        var builder = new StringBuilder();
        var field = 1;
        for (var i = 0; i < candidate.AnchorIds.Length; i++)
        {
            AppendGap(builder, candidate.Gaps[i], typed, ref field);
            Append(builder, tokens.Resolve(candidate.AnchorIds[i]));
        }
        AppendGap(builder, candidate.Gaps[^1], typed, ref field);
        return builder.ToString();
    }

    private void AppendGap(StringBuilder builder, GapStatistics gap, bool typed, ref int field)
    {
        if (gap.Range.Maximum == 0)
            return;
        if (!typed || gap.Range is not { Minimum: 1, Maximum: 1 })
        {
            Append(builder, $"*{{{gap.Range.Minimum},{gap.Range.Maximum}}}");
            return;
        }
        var parser = gap.SyntaxMatches
            .Select(x => new { x.Key, Coverage = x.Value / (double)Math.Max(1, gap.Samples.Seen), Specificity = syntaxes.Resolve(x.Key)?.Specificity ?? 0 })
            .Where(x => x.Coverage >= threshold)
            .OrderByDescending(x => x.Specificity)
            .ThenByDescending(x => x.Coverage)
            .Select(x => x.Key)
            .FirstOrDefault() ?? "word";
        Append(builder, $"%field{field++}:{parser}%");
    }

    private static void Append(StringBuilder builder, string token)
    {
        if (builder.Length > 0)
            builder.Append(' ');
        builder.Append(token);
    }
}
