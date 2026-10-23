using Rag.Core.Models;
using Rag.Core.Retrieval;

namespace Rag.Core.Tests.Retrieval;

/// <summary>
/// Worked example (k = 60), lists A = [a b c] and B = [b c d]:
///   a = 1/61            = 0.016393
///   b = 1/62 + 1/61     = 0.032522
///   c = 1/63 + 1/62     = 0.032002
///   d = 1/63            = 0.015873
/// fused order: b c a d
/// </summary>
public sealed class ReciprocalRankFusionTests
{
    private const double Tolerance = 1e-12;

    private static VectorSearchResult R(string chunk, string? text = null, double score = 0) =>
        new() { ChunkId = chunk, DocumentId = chunk.Split(':')[0], Text = text ?? $"text of {chunk}", Score = score };

    private static RankedList List(string source, params string[] chunks) =>
        new(source, chunks.Select(c => R(c)).ToArray());

    private static readonly ReciprocalRankFusion Rrf60 = new(60);

    [Fact]
    public void WorkedExample_ProducesTheHandComputedOrderAndScores()
    {
        var fused = Rrf60.Fuse([List("dense", "a", "b", "c"), List("lexical", "b", "c", "d")], 10);

        Assert.Equal(["b", "c", "a", "d"], fused.Select(f => f.Result.ChunkId));
        Assert.Equal(1.0 / 62 + 1.0 / 61, fused[0].Score, Tolerance);
        Assert.Equal(1.0 / 63 + 1.0 / 62, fused[1].Score, Tolerance);
        Assert.Equal(1.0 / 61, fused[2].Score, Tolerance);
        Assert.Equal(1.0 / 63, fused[3].Score, Tolerance);
    }

    [Fact]
    public void DocumentFoundByBothRetrievers_OutranksTopResultOfOnlyOne()
    {
        var fused = Rrf60.Fuse([List("dense", "only-dense", "both"), List("lexical", "only-lex", "both")], 3);

        Assert.Equal("both", fused[0].Result.ChunkId);
    }

    [Fact]
    public void SourceAttribution_ListsEveryListThatContainedTheChunkWithItsRankAndOriginalScore()
    {
        var dense = new RankedList("dense", [R("a", score: 0.9), R("b", score: 0.5)]);
        var lexical = new RankedList("lexical", [R("b", score: 12.5)]);

        var b = Rrf60.Fuse([dense, lexical], 5).Single(f => f.Result.ChunkId == "b");

        Assert.Equal(
            [new SourceRank("dense", 2, 0.5), new SourceRank("lexical", 1, 12.5)],
            b.Sources);
    }

    [Fact]
    public void EqualScores_AreOrderedByBestRankThenChunkId_IndependentOfListOrder()
    {
        // x is rank 1 in one list, y is rank 1 in the other: identical scores and identical best rank.
        var forward = Rrf60.Fuse([List("l1", "y"), List("l2", "x")], 5);
        var reversed = Rrf60.Fuse([List("l2", "x"), List("l1", "y")], 5);

        Assert.Equal(["x", "y"], forward.Select(f => f.Result.ChunkId));
        Assert.Equal(["x", "y"], reversed.Select(f => f.Result.ChunkId));
    }

    [Fact]
    public void EqualScores_PreferTheChunkWithTheBetterBestRank_BeforeFallingBackToChunkId()
    {
        // k = 1. p: rank 2 in l1 (weight 1)      → 1/3
        //        q: rank 1 in l2 (weight 2/3)    → (2/3)/2 = 1/3   (same score, better best rank)
        // Alphabetical order would put p first; best rank must win.
        var fused = new ReciprocalRankFusion(1).Fuse(
        [
            new RankedList("l1", [R("filler"), R("p")], Weight: 1.0),
            new RankedList("l2", [R("q")], Weight: 2.0 / 3)
        ], 5);

        var order = fused.Select(f => f.Result.ChunkId).ToArray();
        Assert.True(Array.IndexOf(order, "q") < Array.IndexOf(order, "p"), string.Join(",", order));
    }

    [Fact]
    public void RepeatedChunkInsideOneList_CountsOnlyAtItsFirstRank()
    {
        var fused = Rrf60.Fuse([List("dense", "a", "a", "a", "b")], 5);

        Assert.Equal(1.0 / 61, fused.Single(f => f.Result.ChunkId == "a").Score, Tolerance);
        Assert.Equal(1.0 / 62, fused.Single(f => f.Result.ChunkId == "b").Score, Tolerance); // rank 2, not 4
    }

    [Fact]
    public void Weights_ScaleEachListsContribution()
    {
        var lists = new[]
        {
            new RankedList("dense", [R("a")], Weight: 1.0),
            new RankedList("lexical", [R("b")], Weight: 3.0)
        };

        var fused = Rrf60.Fuse(lists, 5);

        Assert.Equal(["b", "a"], fused.Select(f => f.Result.ChunkId));
        Assert.Equal(3.0 / 61, fused[0].Score, Tolerance);
    }

    [Fact]
    public void ScoreFollowsOneOverKPlusRank_ForAnyK()
    {
        Assert.Equal(1.0 / 2, new ReciprocalRankFusion(1).Fuse([List("l", "a")], 1)[0].Score, Tolerance);
        Assert.Equal(1.0 / 11, new ReciprocalRankFusion(10).Fuse([List("l", "a")], 1)[0].Score, Tolerance);
    }

    [Fact]
    public void TextAndMetadata_AreTakenFromTheFirstListContainingTheChunk()
    {
        var first = new RankedList("dense", [R("a", text: "from dense")]);
        var second = new RankedList("lexical", [R("a", text: "from lexical")]);

        Assert.Equal("from dense", Rrf60.Fuse([first, second], 1)[0].Result.Text);
    }

    [Fact]
    public void TopK_TruncatesAfterFusion()
    {
        var fused = Rrf60.Fuse([List("a", "1", "2", "3"), List("b", "4", "5", "6")], 2);

        Assert.Equal(2, fused.Count);
    }

    [Fact]
    public void EmptyInput_YieldsEmptyResult()
    {
        Assert.Empty(Rrf60.Fuse([], 5));
        Assert.Empty(Rrf60.Fuse([List("dense"), List("lexical")], 5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveK_IsRejected(int k) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReciprocalRankFusion(k));

    [Fact]
    public void NonPositiveTopK_AndNegativeWeight_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Rrf60.Fuse([List("a", "x")], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rrf60.Fuse([new RankedList("a", [R("x")], Weight: -1)], 1));
    }
}
