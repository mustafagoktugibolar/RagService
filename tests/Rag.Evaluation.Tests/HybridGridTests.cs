using Rag.Evaluation.Tuning;

namespace Rag.Evaluation.Tests;

public sealed class HybridGridTests
{
    private static GridRow Row(double weight, int k, int candidates, double ndcg10, double ndcg5 = 0.5) =>
        new(new HybridConfig(weight, k, candidates), ndcg10, ndcg5);

    [Fact]
    public void Grid_IsTheFullCartesianProductDeclaredInTheProtocol()
    {
        var grid = HybridGrid.Configurations().ToList();

        Assert.Equal(36, grid.Count);
        Assert.Equal(36, grid.Distinct().Count());
        Assert.Equal([0.25, 0.5, 0.75, 1.0], grid.Select(c => c.LexicalWeight).Distinct().Order());
        Assert.Equal([10, 30, 60], grid.Select(c => c.RrfK).Distinct().Order());
        Assert.Equal([10, 20, 50], grid.Select(c => c.CandidateK).Distinct().Order());
    }

    [Fact]
    public void Select_PicksTheHighestNdcg10()
    {
        var best = HybridGrid.Select([Row(1, 60, 50, 0.80), Row(0.5, 30, 20, 0.85), Row(0.25, 10, 10, 0.82)]);

        Assert.Equal(new HybridConfig(0.5, 30, 20), best);
    }

    [Fact]
    public void Select_TiesOnNdcg10FallBackToNdcg5()
    {
        var best = HybridGrid.Select([Row(1, 60, 50, 0.85, ndcg5: 0.70), Row(0.5, 30, 20, 0.85, ndcg5: 0.75)]);

        Assert.Equal(new HybridConfig(0.5, 30, 20), best);
    }

    [Fact]
    public void Select_FullTiesPreferTheSmallerCandidateSet_ThenLowerLexicalWeight_ThenSmallerK()
    {
        var candidates = HybridGrid.Select([Row(0.5, 30, 50, 0.85), Row(0.5, 30, 10, 0.85), Row(0.5, 30, 20, 0.85)]);
        var weights = HybridGrid.Select([Row(0.75, 30, 20, 0.85), Row(0.25, 30, 20, 0.85)]);
        var ks = HybridGrid.Select([Row(0.5, 60, 20, 0.85), Row(0.5, 10, 20, 0.85)]);

        Assert.Equal(10, candidates.CandidateK);
        Assert.Equal(0.25, weights.LexicalWeight);
        Assert.Equal(10, ks.RrfK);
    }

    [Fact]
    public void Select_ComparesMetricsAfterRoundingToFourDecimals()
    {
        // 0.85000 vs 0.85004 are the same score for selection; the smaller candidate set wins.
        var best = HybridGrid.Select([Row(0.5, 30, 50, 0.85004), Row(0.5, 30, 10, 0.85000)]);

        Assert.Equal(10, best.CandidateK);
    }

    [Fact]
    public void Select_OfNothing_Throws() =>
        Assert.Throws<InvalidOperationException>(() => HybridGrid.Select([]));
}
