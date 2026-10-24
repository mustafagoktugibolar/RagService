using Rag.Evaluation.Human;

namespace Rag.Evaluation.Tests.Human;

/// <summary>
/// Expected values were computed independently (separate Python implementation of Cohen's kappa) for:
/// A = 0 0 1 1 2 2 3 3 2 1 0 3, B = 0 1 1 2 2 3 3 3 2 0 0 2 (12 items, 4 grades).
/// </summary>
public sealed class AgreementMetricsTests
{
    private static readonly int[] A = [0, 0, 1, 1, 2, 2, 3, 3, 2, 1, 0, 3];
    private static readonly int[] B = [0, 1, 1, 2, 2, 3, 3, 3, 2, 0, 0, 2];

    [Fact]
    public void QuadraticWeightedKappa_MatchesTheIndependentComputation() =>
        Assert.Equal(0.8333333333333334, AgreementMetrics.WeightedKappa(A, B, 4)!.Value, 12);

    [Fact]
    public void UnweightedKappa_IsMuchLowerWhenDisagreementsAreAllOneStepApart() =>
        Assert.Equal(0.4444444444444445, AgreementMetrics.Kappa(A, B, 4)!.Value, 12);

    [Fact]
    public void BinaryKappa_AtGradeTwoAndAbove_MatchesTheIndependentComputation() =>
        Assert.Equal(0.8333333333333333, AgreementMetrics.BinaryKappa(A, B, threshold: 2)!.Value, 12);

    [Fact]
    public void ExactAndAdjacentAgreement_AreFractionsOfItems()
    {
        Assert.Equal(7.0 / 12, AgreementMetrics.ExactAgreement(A, B), 12);
        Assert.Equal(1.0, AgreementMetrics.AdjacentAgreement(A, B), 12);
    }

    [Fact]
    public void ConfusionMatrix_IsRowsOfRaterAAndColumnsOfRaterB()
    {
        var matrix = AgreementMetrics.Confusion(A, B, 4);

        Assert.Equal([2, 1, 0, 0], Row(matrix, 0));
        Assert.Equal([1, 1, 1, 0], Row(matrix, 1));
        Assert.Equal([0, 0, 2, 1], Row(matrix, 2));
        Assert.Equal([0, 0, 1, 2], Row(matrix, 3));
    }

    [Fact]
    public void PerfectAgreement_GivesKappaOne()
    {
        Assert.Equal(1.0, AgreementMetrics.WeightedKappa(A, A, 4)!.Value, 12);
        Assert.Equal(1.0, AgreementMetrics.Kappa(A, A, 4)!.Value, 12);
    }

    [Fact]
    public void WhenBothRatersGiveEveryItemTheSameGrade_KappaIsUndefinedNotOne()
    {
        // pe = 1 makes the denominator zero: agreement carries no information, so the metric must not claim "perfect".
        int[] same = [2, 2, 2, 2];

        Assert.Null(AgreementMetrics.WeightedKappa(same, same, 4));
        Assert.Null(AgreementMetrics.BinaryKappa(same, same, 2));
    }

    [Fact]
    public void SystematicOppositeRatings_GiveNegativeKappa()
    {
        int[] a = [0, 0, 3, 3, 0, 3];
        int[] b = [3, 3, 0, 0, 3, 0];

        Assert.True(AgreementMetrics.WeightedKappa(a, b, 4)!.Value < -0.9);
    }

    [Fact]
    public void ABigDisagreementCostsMoreThanASmallOne_UnderQuadraticWeights()
    {
        int[] reference = [0, 1, 2, 3, 0, 1, 2, 3];
        int[] oneOff = [0, 1, 2, 2, 0, 1, 2, 3];       // one item off by 1
        int[] threeOff = [0, 1, 2, 0, 0, 1, 2, 3];     // the same item off by 3

        Assert.True(AgreementMetrics.WeightedKappa(reference, oneOff, 4)!.Value > AgreementMetrics.WeightedKappa(reference, threeOff, 4)!.Value);
    }

    [Fact]
    public void ClusterBootstrap_ResamplesWholeQueries_AndBracketsTheObservedKappa()
    {
        // 6 queries x 2 items; items of one query move together
        string[] queries = ["q1", "q1", "q2", "q2", "q3", "q3", "q4", "q4", "q5", "q5", "q6", "q6"];

        var ci = AgreementMetrics.WeightedKappaInterval(queries, A, B, 4, resamples: 2000, seed: 7);

        Assert.True(ci.Lower <= ci.Observed && ci.Observed <= ci.Upper, $"{ci.Lower} {ci.Observed} {ci.Upper}");
        Assert.Equal(0.8333333333333334, ci.Observed, 12);
        Assert.True(ci.Upper - ci.Lower > 0.05, "with only six queries the interval must be wide");
    }

    [Fact]
    public void ClusterBootstrap_IsReproducibleForASeed()
    {
        string[] queries = ["q1", "q1", "q2", "q2", "q3", "q3", "q4", "q4", "q5", "q5", "q6", "q6"];

        Assert.Equal(
            AgreementMetrics.WeightedKappaInterval(queries, A, B, 4, resamples: 500, seed: 3),
            AgreementMetrics.WeightedKappaInterval(queries, A, B, 4, resamples: 500, seed: 3));
    }

    [Fact]
    public void MismatchedOrEmptyInputs_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => AgreementMetrics.WeightedKappa([0, 1], [0], 4));
        Assert.Throws<ArgumentException>(() => AgreementMetrics.WeightedKappa([], [], 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgreementMetrics.WeightedKappa([0, 5], [0, 1], 4));   // grade outside 0..3
    }

    private static int[] Row(int[,] matrix, int row) => Enumerable.Range(0, matrix.GetLength(1)).Select(c => matrix[row, c]).ToArray();
}
