using Rag.Evaluation.Harness;

namespace Rag.Evaluation.Tests;

/// <summary>
/// Corpus: d1 "apple banana", d2 "banana cherry", d3 "cherry date" (N = 3, every doc has 2 tokens, avgdl = 2).
/// BM25 (Lucene variant): idf = ln(1 + (N - n + 0.5) / (n + 0.5)); k1 = 1.2, b = 0.75.
/// For tf = 1 and dl = avgdl the tf component is tf·(k1+1)/(tf+k1) = 2.2/2.2 = 1, so score = idf.
/// </summary>
public sealed class Bm25IndexTests
{
    private static Bm25Index Index() => new(
    [
        ("d1", ["apple", "banana"]),
        ("d2", ["banana", "cherry"]),
        ("d3", ["cherry", "date"])
    ]);

    [Fact]
    public void RareTerm_ScoresItsIdf()
    {
        var hits = Index().Search(["apple"]);

        var hit = Assert.Single(hits);
        Assert.Equal("d1", hit.Id);
        Assert.Equal(Math.Log(1 + 2.5 / 1.5), hit.Score, 1e-12);   // n = 1 → 0.980829…
    }

    [Fact]
    public void CommonTerm_HasLowerIdfAndTiesAreBrokenById()
    {
        var hits = Index().Search(["banana"]);

        Assert.Equal(["d1", "d2"], hits.Select(h => h.Id));
        Assert.Equal(Math.Log(1 + 1.5 / 2.5), hits[0].Score, 1e-12);   // n = 2 → 0.470004…
        Assert.Equal(hits[0].Score, hits[1].Score, 1e-12);
    }

    [Fact]
    public void MultiTermQuery_SumsTermScores()
    {
        var hits = Index().Search(["apple", "banana"]);

        Assert.Equal("d1", hits[0].Id);
        Assert.Equal(Math.Log(1 + 2.5 / 1.5) + Math.Log(1 + 1.5 / 2.5), hits[0].Score, 1e-12);
    }

    [Fact]
    public void RepeatedTermsSaturate()
    {
        var index = new Bm25Index([("long", ["x", "x", "x", "x", "y"]), ("short", ["x", "z"])]);

        var once = index.Search(["x"]).Single(h => h.Id == "short").Score;
        var many = index.Search(["x"]).Single(h => h.Id == "long").Score;

        Assert.True(many < once * 4, "four occurrences must score far less than four times one occurrence");
    }

    [Fact]
    public void UnknownTermsAndEmptyQueries_ReturnNothing()
    {
        Assert.Empty(Index().Search(["zebra"]));
        Assert.Empty(Index().Search([]));
    }

    [Fact]
    public void AllowedIds_RestrictResultsButNotTheIdfStatistics()
    {
        var all = Index().Search(["banana"]);
        var restricted = Index().Search(["banana"], allowedIds: new HashSet<string> { "d2" });

        Assert.Equal("d2", Assert.Single(restricted).Id);
        Assert.Equal(all.Single(h => h.Id == "d2").Score, restricted[0].Score, 1e-12);
    }
}
