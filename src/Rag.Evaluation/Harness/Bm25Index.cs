namespace Rag.Evaluation.Harness;

/// <summary>
/// Minimal in-memory BM25 (Lucene variant) over pre-tokenised documents, used by the offline harness as the lexical
/// leg. idf = ln(1 + (N − n + 0.5)/(n + 0.5)); tf component = tf·(k1+1) / (tf + k1·(1 − b + b·dl/avgdl)).
/// Statistics come from the whole index; an optional allow-list restricts which documents may be returned, so metadata
/// filters change the candidate set but not term weights. Ties are ordered by id for reproducible rankings.
/// </summary>
public sealed class Bm25Index
{
    private readonly double _k1;
    private readonly double _b;
    private readonly Dictionary<string, Dictionary<string, int>> _termFrequencies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lengths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly double _averageLength;

    public Bm25Index(IEnumerable<(string Id, IReadOnlyList<string> Tokens)> documents, double k1 = 1.2, double b = 0.75)
    {
        _k1 = k1;
        _b = b;

        foreach (var (id, tokens) in documents)
        {
            var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var token in tokens)
            {
                frequencies[token] = frequencies.GetValueOrDefault(token) + 1;
            }

            _termFrequencies[id] = frequencies;
            _lengths[id] = tokens.Count;
            foreach (var term in frequencies.Keys)
            {
                _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
            }
        }

        _averageLength = _lengths.Count == 0 ? 0 : _lengths.Values.Average();
    }

    public IReadOnlyList<(string Id, double Score)> Search(
        IReadOnlyList<string> queryTokens,
        IReadOnlySet<string>? allowedIds = null)
    {
        var count = _lengths.Count;
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var term in queryTokens.Distinct(StringComparer.Ordinal))
        {
            if (!_documentFrequency.TryGetValue(term, out var documentFrequency))
            {
                continue;
            }

            var idf = Math.Log(1 + (count - documentFrequency + 0.5) / (documentFrequency + 0.5));
            foreach (var (id, frequencies) in _termFrequencies)
            {
                if (!frequencies.TryGetValue(term, out var tf) || (allowedIds is not null && !allowedIds.Contains(id)))
                {
                    continue;
                }

                var norm = _k1 * (1 - _b + _b * _lengths[id] / _averageLength);
                scores[id] = scores.GetValueOrDefault(id) + idf * tf * (_k1 + 1) / (tf + norm);
            }
        }

        return scores
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, pair.Value))
            .ToArray();
    }
}
