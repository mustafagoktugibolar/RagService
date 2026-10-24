using Rag.Evaluation.Human;

namespace Rag.Evaluation.Tests.Human;

public sealed class PoolCoverageTests
{
    [Fact]
    public void Overlap_IsTheShareOfTheTopKTheyHaveInCommon()
    {
        string[] a = ["1", "2", "3", "4", "5"];
        string[] b = ["3", "1", "9", "8", "7"];

        Assert.Equal(2 / 5.0, PoolCoverage.Overlap(a, b, 5), 12);
        Assert.Equal(1 / 2.0, PoolCoverage.Overlap(a, b, 2), 12);   // top-2: {1,2} vs {3,1} share one
    }

    [Fact]
    public void Coverage_IsTheShareOfAReferenceTopKFoundInAPool()
    {
        string[] reference = ["a", "b", "c", "d", "e"];
        var pool = new HashSet<string> { "a", "c", "z", "y" };

        Assert.Equal(2 / 5.0, PoolCoverage.Coverage(reference, pool, 5), 12);
        Assert.Equal(1 / 2.0, PoolCoverage.Coverage(reference, pool, 2), 12);
    }

    [Fact]
    public void AnEmptyReference_HasFullCoverage_NotADivisionByZero() =>
        Assert.Equal(1.0, PoolCoverage.Coverage([], new HashSet<string>(), 5));

    [Fact]
    public void TheChanceLevel_IsThePoolSizeOverTheCorpusSize_SoCoverageCanBeReadAgainstLuck()
    {
        Assert.Equal(10 / 35.0, PoolCoverage.ChanceCoverage(poolSize: 10, corpusSize: 35), 12);
        Assert.Equal(1.0, PoolCoverage.ChanceCoverage(poolSize: 40, corpusSize: 35));   // a pool larger than the corpus covers everything by construction
    }

    [Fact]
    public void ProxyQueries_AreSeeded_UseMidFrequencyTermsOnly_AndComeFromDistinctChunks()
    {
        var chunks = Enumerable.Range(0, 40).Select(i => new PilotChunk
        {
            ChunkId = $"d:{i:D6}", DocumentId = "d", Language = "en",
            Text = $"alpha beta common words everywhere topic{i % 8} rare{i} shared{i % 5} qualifier{i % 5} extra{i % 8}"
        }).ToList();

        var first = ProxyQueries.Generate(chunks, count: 10, termsPerQuery: 3, seed: 5);
        var second = ProxyQueries.Generate(chunks, count: 10, termsPerQuery: 3, seed: 5);

        Assert.Equal(first.Select(q => q.Text), second.Select(q => q.Text));
        Assert.Equal(10, first.Select(q => q.SourceChunkId).Distinct().Count());
        Assert.All(first, q =>
        {
            var words = q.Text.Split(' ');
            Assert.Equal(3, words.Length);
            Assert.DoesNotContain("alpha", words);                 // in every chunk: carries no signal
            Assert.DoesNotContain(words, w => w.StartsWith("rare"));   // in one chunk only: too specific
        });
        Assert.NotEqual(first.Select(q => q.Text), ProxyQueries.Generate(chunks, 10, 3, seed: 6).Select(q => q.Text));
    }

    [Fact]
    public void SystemContribution_CountsWhatEachSystemAddsThatNoOtherSystemFound()
    {
        var rankings = new Dictionary<string, IReadOnlyList<string>>
        {
            ["A"] = ["1", "2", "3"],
            ["B"] = ["3", "4", "5"],
            ["C"] = ["5", "6", "1"]
        };

        var contribution = PoolCoverage.UniqueContribution(rankings, depth: 3);

        Assert.Equal(1, contribution["A"]);   // "2"
        Assert.Equal(1, contribution["B"]);   // "4"
        Assert.Equal(1, contribution["C"]);   // "6"
    }

    [Fact]
    public void UnionSize_AndPairwiseJaccard_AreComputedOverTheTopDepth()
    {
        var rankings = new Dictionary<string, IReadOnlyList<string>> { ["A"] = ["1", "2", "3"], ["B"] = ["2", "3", "4"] };

        Assert.Equal(4, PoolCoverage.UnionSize(rankings, 3));
        Assert.Equal(2 / 4.0, PoolCoverage.Jaccard(rankings["A"], rankings["B"], 3), 12);
    }
}
