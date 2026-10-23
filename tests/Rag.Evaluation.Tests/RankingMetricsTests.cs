using Rag.Evaluation.Metrics;

namespace Rag.Evaluation.Tests;

/// <summary>
/// Every expected value here was computed by hand (worked example in docs/kb/07-retrieval-evaluation.md).
/// Ranking: d3 d1 d7 d2 d9   Judgments: d1=3, d2=2, d4=1  (R = 3 relevant, d4 is never retrieved)
/// </summary>
public sealed class RankingMetricsTests
{
    private static readonly string[] Ranked = ["d3", "d1", "d7", "d2", "d9"];

    private static readonly Dictionary<string, int> Judgments = new() { ["d1"] = 3, ["d2"] = 2, ["d4"] = 1 };

    private const double Tolerance = 1e-9;

    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(2, 1.0)]
    [InlineData(5, 1.0)]
    public void HitAtK(int k, double expected) =>
        Assert.Equal(expected, RankingMetrics.HitAtK(Ranked, Judgments, k));

    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(2, 0.5)]
    [InlineData(3, 1.0 / 3)]
    [InlineData(5, 0.4)]
    public void PrecisionAtK(int k, double expected) =>
        Assert.Equal(expected, RankingMetrics.PrecisionAtK(Ranked, Judgments, k)!.Value, Tolerance);

    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(2, 1.0 / 3)]
    [InlineData(4, 2.0 / 3)]
    [InlineData(5, 2.0 / 3)]
    public void RecallAtK(int k, double expected) =>
        Assert.Equal(expected, RankingMetrics.RecallAtK(Ranked, Judgments, k)!.Value, Tolerance);

    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(2, 0.5)]
    [InlineData(5, 0.5)]
    public void MrrAtK(int k, double expected) =>
        Assert.Equal(expected, RankingMetrics.MrrAtK(Ranked, Judgments, k)!.Value, Tolerance);

    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(3, 0.47020199776783905)]
    [InlineData(5, 0.6077574819118444)]
    public void NdcgAtK_UsesExponentialGainAndLog2Discount(int k, double expected) =>
        Assert.Equal(expected, RankingMetrics.NdcgAtK(Ranked, Judgments, k)!.Value, 1e-12);

    [Fact]
    public void AveragePrecisionAtK_DividesByMinOfRelevantAndK()
    {
        // relevant at ranks 2 and 4: (1/2 + 2/4) / min(R=3, K=5) = 1/3
        Assert.Equal(1.0 / 3, RankingMetrics.AveragePrecisionAtK(Ranked, Judgments, 5)!.Value, Tolerance);
        // K=2: only rank 2 counts: (1/2) / min(3, 2) = 1/4
        Assert.Equal(0.25, RankingMetrics.AveragePrecisionAtK(Ranked, Judgments, 2)!.Value, Tolerance);
    }

    [Fact]
    public void PerfectRanking_ScoresOneOnEveryMetric()
    {
        string[] perfect = ["d1", "d2", "d4"];
        Assert.Equal(1.0, RankingMetrics.NdcgAtK(perfect, Judgments, 3)!.Value, Tolerance);
        Assert.Equal(1.0, RankingMetrics.PrecisionAtK(perfect, Judgments, 3)!.Value, Tolerance);
        Assert.Equal(1.0, RankingMetrics.RecallAtK(perfect, Judgments, 3)!.Value, Tolerance);
        Assert.Equal(1.0, RankingMetrics.MrrAtK(perfect, Judgments, 3)!.Value, Tolerance);
        Assert.Equal(1.0, RankingMetrics.AveragePrecisionAtK(perfect, Judgments, 3)!.Value, Tolerance);
    }

    [Fact]
    public void Ndcg_RewardsPuttingTheHighestGradeFirst()
    {
        var gradedFirst = RankingMetrics.NdcgAtK(["d1", "d2"], Judgments, 2)!.Value;
        var gradedSwapped = RankingMetrics.NdcgAtK(["d2", "d1"], Judgments, 2)!.Value;
        Assert.True(gradedFirst > gradedSwapped);
    }

    [Fact]
    public void Precision_DenominatorIsK_EvenWhenFewerResultsAreReturned() =>
        Assert.Equal(0.2, RankingMetrics.PrecisionAtK(["d1"], Judgments, 5)!.Value, Tolerance);

    [Fact]
    public void EmptyRanking_ScoresZeroWhenRelevantDocumentsExist()
    {
        Assert.Equal(0.0, RankingMetrics.HitAtK([], Judgments, 5));
        Assert.Equal(0.0, RankingMetrics.RecallAtK([], Judgments, 5));
        Assert.Equal(0.0, RankingMetrics.NdcgAtK([], Judgments, 5));
        Assert.Equal(0.0, RankingMetrics.MrrAtK([], Judgments, 5));
    }

    [Fact]
    public void QueryWithoutRelevantDocuments_IsUndefinedNotZero()
    {
        var none = new Dictionary<string, int>();
        Assert.Null(RankingMetrics.HitAtK(Ranked, none, 5));
        Assert.Null(RankingMetrics.PrecisionAtK(Ranked, none, 5));
        Assert.Null(RankingMetrics.RecallAtK(Ranked, none, 5));
        Assert.Null(RankingMetrics.MrrAtK(Ranked, none, 5));
        Assert.Null(RankingMetrics.NdcgAtK(Ranked, none, 5));
        Assert.Null(RankingMetrics.AveragePrecisionAtK(Ranked, none, 5));
    }

    [Fact]
    public void GradeZeroEntries_AreNotRelevant()
    {
        var judgments = new Dictionary<string, int> { ["d1"] = 0, ["d2"] = 1 };
        Assert.Equal(1.0, RankingMetrics.RecallAtK(["d2"], judgments, 5)!.Value, Tolerance);
        Assert.Equal(0.0, RankingMetrics.HitAtK(["d1"], judgments, 5));
    }

    [Fact]
    public void DuplicateIdsInARanking_CountOnceAtTheirFirstPosition()
    {
        // [d1, d1, d2] behaves exactly like [d1, d2]
        string[] withDuplicates = ["d1", "d1", "d2"];
        string[] deduped = ["d1", "d2"];
        Assert.Equal(
            RankingMetrics.NdcgAtK(deduped, Judgments, 3),
            RankingMetrics.NdcgAtK(withDuplicates, Judgments, 3));
        Assert.Equal(
            RankingMetrics.PrecisionAtK(deduped, Judgments, 3),
            RankingMetrics.PrecisionAtK(withDuplicates, Judgments, 3));
        Assert.Equal(2.0 / 3, RankingMetrics.RecallAtK(withDuplicates, Judgments, 3)!.Value, Tolerance);
    }

    [Fact]
    public void RelevantDocumentsBeyondK_StillCountInTheRecallDenominator() =>
        Assert.Equal(0.5, RankingMetrics.RecallAtK(["d1", "x"], new Dictionary<string, int> { ["d1"] = 1, ["d2"] = 1 }, 2)!.Value, Tolerance);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveK_Throws(int k) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RankingMetrics.HitAtK(Ranked, Judgments, k));
}
