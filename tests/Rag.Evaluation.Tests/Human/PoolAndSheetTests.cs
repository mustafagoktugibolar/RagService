using Rag.Evaluation.Human;

namespace Rag.Evaluation.Tests.Human;

public sealed class CsvTests
{
    [Fact]
    public void RoundTrips_CommasQuotesNewlinesAndTurkishLetters()
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "id", "text" },
            new[] { "1", "plain" },
            new[] { "2", "comma, inside" },
            new[] { "3", "she said \"hi\"" },
            new[] { "4", "line one\nline two\r\nline three" },
            new[] { "5", "Çalışan işçi — ğüş" },
            new[] { "6", "" }
        };

        var parsed = Csv.Read(Csv.Write(rows));

        Assert.Equal(rows.Count, parsed.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(rows[i], parsed[i]);   // exact, including the \r\n inside the quoted field
        }

        Assert.Equal("comma, inside", parsed[2][1]);
        Assert.Equal("she said \"hi\"", parsed[3][1]);
        Assert.Equal("Çalışan işçi — ğüş", parsed[5][1]);
        Assert.Equal("", parsed[6][1]);
    }

    [Fact]
    public void BlankLinesAreIgnored_AndATrailingRowWithoutNewlineIsKept() =>
        Assert.Equal(2, Csv.Read("a,b\n\n1,2").Count);
}

public sealed class PoolAndSheetTests
{
    private static readonly PilotChunk[] Chunks = Enumerable.Range(0, 12).Select(i => new PilotChunk
    {
        ChunkId = $"doc{i / 3}:{i % 3:D6}", DocumentId = $"doc{i / 3}", Title = $"Title {i / 3}", Language = "en", Text = $"passage text number {i} about subject{i}"
    }).ToArray();

    private static readonly PilotQuery[] Queries =
    [
        new() { Id = "pilot-001", Text = "first query", Category = "keyword", Language = "en", WrittenBy = "owner" },
        new() { Id = "pilot-002", Text = "second query", Category = "paraphrase", Language = "en", WrittenBy = "owner" }
    ];

    private static PoolSystem Fixed(string name, params string[] ids) => new(name, (_, depth) => ids.Take(depth).ToList());

    private static PoolKey Pool(int negatives = 2, int seed = 11) => PoolBuilder.Build(
        Chunks, Queries,
        [Fixed("sysA", "doc0:000000", "doc0:000001", "doc1:000000"), Fixed("sysB", "doc0:000001", "doc2:000000", "doc1:000000")],
        depth: 3, randomNegatives: negatives, seed, "test-corpus");

    [Fact]
    public void ThePool_IsTheUnionOfTheSystems_WithProvenanceForEveryCandidate()
    {
        var items = Pool(negatives: 0).Pools["pilot-001"];

        Assert.Equal(["doc0:000000", "doc0:000001", "doc1:000000", "doc2:000000"], items.Select(i => i.ChunkId));
        var both = items.Single(i => i.ChunkId == "doc0:000001");
        Assert.Equal([new PoolSourceRank("sysA", 2), new PoolSourceRank("sysB", 1)], both.Sources);
    }

    [Fact]
    public void RandomNegatives_AreNeverSystemCandidates_AreSeededAndTaggedRandom()
    {
        var pool = Pool(negatives: 3);
        var random = pool.Pools["pilot-001"].Where(i => i.Sources.Any(s => s.Source == PoolBuilder.RandomSource)).ToList();

        Assert.Equal(3, random.Count);
        Assert.All(random, i => Assert.Single(i.Sources));
        Assert.DoesNotContain(random.Select(i => i.ChunkId), id => new[] { "doc0:000000", "doc0:000001", "doc1:000000", "doc2:000000" }.Contains(id));
        Assert.Equal(random.Select(i => i.ChunkId), Pool(negatives: 3).Pools["pilot-001"].Where(i => i.Sources.Any(s => s.Source == "random")).Select(i => i.ChunkId));
    }

    [Fact]
    public void DifferentSeeds_PickDifferentRandomNegatives_DifferentQueriesGetDifferentOnes()
    {
        string Randoms(PoolKey key, string q) => string.Join(",", key.Pools[q].Where(i => i.Sources[0].Source == "random").Select(i => i.ChunkId));

        Assert.NotEqual(Randoms(Pool(seed: 1), "pilot-001"), Randoms(Pool(seed: 2), "pilot-001"));
        Assert.NotEqual(Randoms(Pool(seed: 1), "pilot-001"), Randoms(Pool(seed: 1), "pilot-002"));
    }

    [Fact]
    public void EachSystemsOwnRanking_IsKeptForTheLaterEvaluationOfThePoolingSystems()
    {
        var rankings = Pool().Rankings["pilot-001"];

        Assert.Equal(["doc0:000000", "doc0:000001", "doc1:000000"], rankings["sysA"]);
        Assert.Equal(["doc0:000001", "doc2:000000", "doc1:000000"], rankings["sysB"]);
    }

    [Fact]
    public void UnknownChunkIdsFromASystem_AreDropped() =>
        Assert.DoesNotContain(PoolBuilder.Build(Chunks, Queries, [Fixed("bad", "ghost:1", "doc0:000000")], 3, 0, 1, "c").Pools["pilot-001"], i => i.ChunkId == "ghost:1");

    [Fact]
    public void TheSheetCoversEveryPooledItemExactlyOnce_WithSequentialItemNumbers()
    {
        var pool = Pool();

        var sheet = AnnotationSheets.Build(pool, Queries, Chunks, "owner", 5);

        Assert.Equal(pool.Pools.Sum(p => p.Value.Count), sheet.Count);
        Assert.Equal(Enumerable.Range(1, sheet.Count), sheet.Select(r => r.ItemNo));
        Assert.Equal(sheet.Count, sheet.Select(r => (r.QueryId, r.ChunkId)).Distinct().Count());
    }

    [Fact]
    public void TheSheetIsBlind_NoSystemNameRankOrScoreCanBeRecoveredFromIt()
    {
        var pool = Pool();
        var csv = AnnotationSheets.ToCsv(AnnotationSheets.Build(pool, Queries, Chunks, "owner", 5));

        Assert.DoesNotContain("sysA", csv);
        Assert.DoesNotContain("sysB", csv);
        Assert.DoesNotContain("random", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rank", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("score", csv, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("item_no,query_id,query,chunk_id,document_title,passage,grade,flags,seconds,comment", csv.Split("\r\n")[0]);
    }

    [Fact]
    public void RandomNegatives_AreIndistinguishableFromRealCandidatesOnTheSheet()
    {
        var sheet = AnnotationSheets.Build(Pool(negatives: 3), Queries, Chunks, "owner", 5);

        Assert.All(sheet, row => Assert.False(string.IsNullOrWhiteSpace(row.Passage)));
        Assert.Equal(Chunks.Length, Chunks.Select(c => c.ChunkId).Distinct().Count());   // nothing marks a row as injected
        Assert.Equal(typeof(SheetRow).GetProperties().Select(p => p.Name).Order(), ["ChunkId", "DocumentTitle", "ItemNo", "Passage", "QueryId", "QueryText"]);
    }

    [Fact]
    public void TwoAnnotators_SeeDifferentOrders_ButTheSameAnnotatorSeesTheSameOrderEveryTime()
    {
        var pool = Pool();

        var a1 = AnnotationSheets.Build(pool, Queries, Chunks, "owner", 5).Select(r => r.ChunkId).ToList();
        var a2 = AnnotationSheets.Build(pool, Queries, Chunks, "owner", 5).Select(r => r.ChunkId).ToList();
        var b = AnnotationSheets.Build(pool, Queries, Chunks, "colleague", 5).Select(r => r.ChunkId).ToList();

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.Equal(a1.Order(), b.Order());   // same items, different order
    }

    [Fact]
    public void AReturnedSheet_IsParsedIntoJudgments_AndBlankGradesAreSkipped()
    {
        var sheet = AnnotationSheets.Build(Pool(), Queries, Chunks, "owner", 5);
        var rows = Csv.Read(AnnotationSheets.ToCsv(sheet));
        rows[1][6] = "3"; rows[1][7] = "unclear-query"; rows[1][8] = "42.5"; rows[1][9] = "hard, \"tricky\"";
        rows[2][6] = "0";
        var returned = Csv.Write(rows.Select(r => (IReadOnlyList<string>)r));

        var judgments = AnnotationSheets.ParseCsv(returned, "owner", "guidelines-v1");

        Assert.Equal(2, judgments.Items.Count);
        Assert.Equal(3, judgments.Items[0].Grade);
        Assert.Equal(["unclear-query"], judgments.Items[0].Flags);
        Assert.Equal(42.5, judgments.Items[0].Seconds);
        Assert.Equal("hard, \"tricky\"", judgments.Items[0].Comment);
        Assert.Equal((sheet[0].QueryId, sheet[0].ChunkId), (judgments.Items[0].QueryId, judgments.Items[0].ChunkId));
    }

    [Fact]
    public void ANonIntegerGrade_AndAMissingColumn_AreRejectedClearly()
    {
        var bad = Csv.Write([["query_id", "chunk_id", "grade"], ["pilot-001", "doc0:000000", "high"]]);

        Assert.Contains(Assert.Throws<PilotValidationException>(() => AnnotationSheets.ParseCsv(bad, "owner", "v1")).Errors, e => e.Contains("not an integer"));
        Assert.Contains(Assert.Throws<PilotValidationException>(() => AnnotationSheets.ParseCsv("a,b\n1,2", "owner", "v1")).Errors, e => e.Contains("missing column"));
    }

    [Fact]
    public void TheBuiltInSystems_RankAnObviousMatchFirst()
    {
        var bm25 = PoolBuilder.Bm25(Chunks);
        var dense = PoolBuilder.HashingDense(Chunks);

        Assert.Equal("doc2:000000", bm25.Search("subject6 passage", 3)[0]);
        Assert.Equal("doc2:000000", dense.Search("subject6 passage", 3)[0]);
    }
}
