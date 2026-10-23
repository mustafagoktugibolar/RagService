namespace Rag.Evaluation.Tuning;

public sealed record HybridConfig(double LexicalWeight, int RrfK, int CandidateK);

/// <summary>Tuning-split result of one configuration.</summary>
public sealed record GridRow(HybridConfig Config, double Ndcg10, double Ndcg5);

/// <summary>The search space and selection order exactly as pre-registered in docs/eval/protocol-hybrid-tuning.md.</summary>
public static class HybridGrid
{
    public static IEnumerable<HybridConfig> Configurations()
    {
        foreach (var weight in new[] { 0.25, 0.5, 0.75, 1.0 })
        {
            foreach (var k in new[] { 10, 30, 60 })
            {
                foreach (var candidates in new[] { 10, 20, 50 })
                {
                    yield return new HybridConfig(weight, k, candidates);
                }
            }
        }
    }

    /// <summary>nDCG@10 ↓, nDCG@5 ↓, CandidateK ↑, LexicalWeight ↑, RrfK ↑, comparing metrics rounded to 4 decimals.</summary>
    public static HybridConfig Select(IReadOnlyList<GridRow> rows)
    {
        if (rows.Count == 0)
        {
            throw new InvalidOperationException("No grid rows to select from.");
        }

        return rows
            .OrderByDescending(r => Math.Round(r.Ndcg10, 4))
            .ThenByDescending(r => Math.Round(r.Ndcg5, 4))
            .ThenBy(r => r.Config.CandidateK)
            .ThenBy(r => r.Config.LexicalWeight)
            .ThenBy(r => r.Config.RrfK)
            .First().Config;
    }
}
