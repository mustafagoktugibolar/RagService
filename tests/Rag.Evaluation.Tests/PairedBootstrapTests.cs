using Rag.Evaluation.Tuning;

namespace Rag.Evaluation.Tests;

public sealed class PairedBootstrapTests
{
    [Fact]
    public void IdenticalScores_GiveAZeroDifferenceAndAZeroWidthInterval()
    {
        double[] scores = [0.2, 0.5, 0.9, 0.4];

        var result = PairedBootstrap.MeanDifference(scores, scores);

        Assert.Equal(0, result.MeanDifference, 12);
        Assert.Equal(0, result.Lower, 12);
        Assert.Equal(0, result.Upper, 12);
    }

    [Fact]
    public void ConstantImprovement_GivesThatImprovementAsTheWholeInterval()
    {
        double[] baseline = [0.2, 0.5, 0.9, 0.4];
        var better = baseline.Select(v => v + 0.1).ToArray();

        var result = PairedBootstrap.MeanDifference(baseline, better);

        Assert.Equal(0.1, result.MeanDifference, 9);
        Assert.Equal(0.1, result.Lower, 9);
        Assert.Equal(0.1, result.Upper, 9);
    }

    [Fact]
    public void ConsistentGains_ExcludeZero()
    {
        double[] baseline = [0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5];
        double[] better = [0.9, 0.8, 1.0, 0.9, 0.7, 0.8, 0.9, 1.0];

        var result = PairedBootstrap.MeanDifference(baseline, better);

        Assert.True(result.Lower > 0, $"lower bound {result.Lower}");
    }

    [Fact]
    public void MixedWinsAndLosses_StraddleZero()
    {
        double[] baseline = [0.5, 0.5, 0.5, 0.5, 0.5, 0.5];
        double[] other = [1.0, 0.0, 1.0, 0.0, 1.0, 0.0];

        var result = PairedBootstrap.MeanDifference(baseline, other);

        Assert.True(result.Lower < 0 && result.Upper > 0, $"[{result.Lower}, {result.Upper}]");
    }

    [Fact]
    public void SameSeed_IsReproducible_DifferentSeedMayDiffer()
    {
        double[] a = [0.1, 0.9, 0.4, 0.6, 0.3];
        double[] b = [0.3, 0.7, 0.9, 0.5, 0.8];

        Assert.Equal(PairedBootstrap.MeanDifference(a, b, seed: 7), PairedBootstrap.MeanDifference(a, b, seed: 7));
    }

    [Fact]
    public void IntervalBracketsTheObservedMean()
    {
        double[] a = [0.1, 0.9, 0.4, 0.6, 0.3, 0.2];
        double[] b = [0.3, 0.7, 0.9, 0.5, 0.8, 0.9];

        var result = PairedBootstrap.MeanDifference(a, b);

        Assert.True(result.Lower <= result.MeanDifference && result.MeanDifference <= result.Upper);
    }

    [Fact]
    public void MismatchedOrEmptyInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => PairedBootstrap.MeanDifference([1.0], [1.0, 2.0]));
        Assert.Throws<ArgumentException>(() => PairedBootstrap.MeanDifference([], []));
    }
}
