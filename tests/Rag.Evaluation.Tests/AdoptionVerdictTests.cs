using Rag.Evaluation.Tuning;

namespace Rag.Evaluation.Tests;

public sealed class AdoptionVerdictTests
{
    private static BootstrapResult Ci(double mean, double lower, double upper) => new(mean, lower, upper, 19);

    private static readonly IReadOnlyDictionary<string, CategoryDifference> NoLoss = new Dictionary<string, CategoryDifference>
    {
        ["keyword"] = new(7, 0.90, 0.92),
        ["paraphrase"] = new(8, 0.80, 0.85)
    };

    [Fact]
    public void AllThreeConditionsHolding_IsACandidate()
    {
        var verdict = HybridTuningRun.Judge(Ci(0.04, 0.01, 0.07), Ci(0.02, -0.02, 0.06), NoLoss);

        Assert.True(verdict.Candidate);
    }

    [Fact]
    public void AnIntervalTouchingZero_FailsEvenWithAPositiveMean()
    {
        var verdict = HybridTuningRun.Judge(Ci(0.04, 0.0, 0.08), Ci(0.02, 0, 0.04), NoLoss);

        Assert.False(verdict.IntervalLowerBoundAboveZero);
        Assert.False(verdict.Candidate);
    }

    [Fact]
    public void HarmAtTheTopRank_Fails_EvenWhenTheOverallIntervalIsPositive()
    {
        var verdict = HybridTuningRun.Judge(Ci(0.04, 0.01, 0.07), Ci(-0.01, -0.05, 0.03), NoLoss);

        Assert.False(verdict.TopRankNotWorse);
        Assert.False(verdict.Candidate);
    }

    [Fact]
    public void ALargeCategoryLoss_Fails()
    {
        var categories = new Dictionary<string, CategoryDifference>
        {
            ["keyword"] = new(7, 0.90, 0.99),
            ["paraphrase"] = new(8, 0.80, 0.74)     // −0.06 beyond the 0.05 tolerance
        };

        var verdict = HybridTuningRun.Judge(Ci(0.04, 0.01, 0.07), Ci(0.02, 0, 0.04), categories);

        Assert.False(verdict.NoLargeCategoryLoss);
        Assert.False(verdict.Candidate);
    }

    [Fact]
    public void ALossExactlyAtTheTolerance_IsAllowed_AndTinyCategoriesAreIgnored()
    {
        var categories = new Dictionary<string, CategoryDifference>
        {
            ["paraphrase"] = new(8, 0.80, 0.75),    // −0.05, allowed
            ["conceptual"] = new(1, 0.90, 0.10)     // only one query: not judged
        };

        Assert.True(HybridTuningRun.Judge(Ci(0.04, 0.01, 0.07), Ci(0.02, 0, 0.04), categories).NoLargeCategoryLoss);
    }
}
