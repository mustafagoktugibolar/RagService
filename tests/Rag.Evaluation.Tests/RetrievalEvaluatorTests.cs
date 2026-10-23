using Rag.Evaluation.Dataset;
using Rag.Evaluation.Evaluation;

namespace Rag.Evaluation.Tests;

public sealed class RetrievalEvaluatorTests
{
    private const double Tolerance = 1e-9;

    private static EvalDataset Dataset => new()
    {
        DatasetVersion = "t",
        Corpus =
        [
            new CorpusDocument { DocumentId = "d1", Text = "alpha beta" },
            new CorpusDocument { DocumentId = "d2", Text = "gamma delta" },
            new CorpusDocument { DocumentId = "d3", Text = "epsilon zeta" }
        ],
        Queries =
        [
            new EvalQuery { Id = "q1", Query = "one", Category = "keyword",
                RelevantDocuments = [new RelevantDocument { DocumentId = "d1", Grade = 3 }] },
            new EvalQuery { Id = "q2", Query = "two", Category = "paraphrase",
                RelevantDocuments = [new RelevantDocument { DocumentId = "d2", Grade = 3 }] },
            new EvalQuery { Id = "q3", Query = "none", Category = "no-answer" }
        ]
    };

    private static RankedHit Hit(string doc, int chunk = 0, string text = "") => new(doc, $"{doc}:{chunk}", text);

    private static RetrieveAsync Fixed(Dictionary<string, RankedHit[]> byQueryId) =>
        (query, _, _) => Task.FromResult<IReadOnlyList<RankedHit>>(byQueryId[query.Id]);

    [Fact]
    public async Task DocumentLevel_AggregatesMeansOverScoredQueriesOnly()
    {
        var retrieve = Fixed(new()
        {
            ["q1"] = [Hit("d1")],                       // relevant at rank 1
            ["q2"] = [Hit("d3"), Hit("d2")],            // relevant at rank 2
            ["q3"] = [Hit("d1")]                        // no-answer: must not influence averages
        });

        var report = await RetrievalEvaluator.EvaluateAsync(Dataset, retrieve,
            new EvaluationOptions { Ks = [1, 2] }, null, CancellationToken.None);

        Assert.Equal(2, report.Document.ScoredQueries);
        Assert.Equal(1, report.Document.SkippedQueries);
        Assert.Equal(0.5, report.Document.ByK[1].Hit, Tolerance);       // (1 + 0) / 2
        Assert.Equal(1.0, report.Document.ByK[2].Hit, Tolerance);       // (1 + 1) / 2
        Assert.Equal(0.75, report.Document.ByK[2].Mrr, Tolerance);      // (1 + 1/2) / 2
        Assert.Equal(0.5, report.Document.ByK[2].Precision, Tolerance);  // each query: 1 relevant in top 2 → 1/2
    }

    [Fact]
    public async Task DocumentLevel_CollapsesChunksOfTheSameDocumentToTheirBestRank()
    {
        var retrieve = Fixed(new()
        {
            ["q1"] = [Hit("d2", 0), Hit("d2", 1), Hit("d1", 0)],
            ["q2"] = [Hit("d2")],
            ["q3"] = []
        });

        var report = await RetrievalEvaluator.EvaluateAsync(Dataset, retrieve,
            new EvaluationOptions { Ks = [2] }, null, CancellationToken.None);

        var q1 = report.Document.PerQuery.Single(q => q.QueryId == "q1");
        Assert.Equal(["d2", "d1"], q1.Ranked);
        Assert.Equal(0.5, q1.ByK[2]!.Mrr, Tolerance);
    }

    [Fact]
    public async Task ByCategory_ReportsEachCategorySeparately_AndOmitsNoAnswerQueries()
    {
        var retrieve = Fixed(new() { ["q1"] = [Hit("d1")], ["q2"] = [Hit("d3")], ["q3"] = [] });

        var report = await RetrievalEvaluator.EvaluateAsync(Dataset, retrieve,
            new EvaluationOptions { Ks = [1] }, null, CancellationToken.None);

        Assert.Equal(1.0, report.Document.ByCategory["keyword"].ByK[1].Hit, Tolerance);
        Assert.Equal(0.0, report.Document.ByCategory["paraphrase"].ByK[1].Hit, Tolerance);
        Assert.DoesNotContain("no-answer", report.Document.ByCategory.Keys);
    }

    [Fact]
    public async Task ChunkLevel_IsJudgedByEvidenceAndIndependentOfDocumentLevel()
    {
        var dataset = Dataset with
        {
            Corpus = [new CorpusDocument { DocumentId = "d1", Text = "intro. the answer is 42. outro." }],
            Queries =
            [
                new EvalQuery { Id = "q1", Query = "meaning",
                    RelevantDocuments = [new RelevantDocument { DocumentId = "d1", Grade = 3, Evidence = "the answer is 42" }] }
            ]
        };
        ChunkRecord[] catalog =
        [
            new("d1", "d1:0", "intro."),
            new("d1", "d1:1", "The answer\nis 42."),
            new("d1", "d1:2", "outro.")
        ];
        var retrieve = Fixed(new() { ["q1"] = [Hit("d1", 0, "intro."), Hit("d1", 1, "The answer\nis 42.")] });

        var report = await RetrievalEvaluator.EvaluateAsync(dataset, retrieve,
            new EvaluationOptions { Ks = [1, 2] }, catalog, CancellationToken.None);

        Assert.Equal(1.0, report.Document.ByK[1].Hit, Tolerance);   // document found at rank 1
        Assert.NotNull(report.Chunk);
        Assert.Equal(0.0, report.Chunk!.ByK[1].Hit, Tolerance);     // but the evidence chunk is only rank 2
        Assert.Equal(0.5, report.Chunk.ByK[2].Mrr, Tolerance);
        Assert.Equal(1.0, report.Chunk.ByK[2].Recall, Tolerance);   // the only evidence chunk was retrieved
    }

    [Fact]
    public async Task ChunkLevel_SkipsQueriesWithoutEvidenceOrExplicitChunkJudgments()
    {
        var retrieve = Fixed(new() { ["q1"] = [Hit("d1")], ["q2"] = [Hit("d2")], ["q3"] = [] });

        var report = await RetrievalEvaluator.EvaluateAsync(Dataset, retrieve,
            new EvaluationOptions { Ks = [1] }, [new ChunkRecord("d1", "d1:0", "alpha beta")], CancellationToken.None);

        Assert.Equal(0, report.Chunk!.ScoredQueries);
        Assert.Equal(3, report.Chunk.SkippedQueries);
    }

    [Fact]
    public async Task ChunkLevel_UsesExplicitChunkIdsWhenGiven()
    {
        var dataset = Dataset with
        {
            Queries = [new EvalQuery { Id = "q1", Query = "x",
                RelevantDocuments = [new RelevantDocument { DocumentId = "d1" }],
                RelevantChunkIds = new() { ["d1:1"] = 2 } }]
        };
        var retrieve = Fixed(new() { ["q1"] = [Hit("d1", 0), Hit("d1", 1)] });

        var report = await RetrievalEvaluator.EvaluateAsync(dataset, retrieve,
            new EvaluationOptions { Ks = [2] }, [], CancellationToken.None);

        Assert.Equal(0.5, report.Chunk!.ByK[2].Mrr, Tolerance);
    }

    [Fact]
    public async Task RetrievesToTheDepthOfTheLargestK()
    {
        var depths = new List<int>();
        RetrieveAsync retrieve = (_, depth, _) =>
        {
            depths.Add(depth);
            return Task.FromResult<IReadOnlyList<RankedHit>>([]);
        };

        await RetrievalEvaluator.EvaluateAsync(Dataset, retrieve,
            new EvaluationOptions { Ks = [1, 3, 10] }, null, CancellationToken.None);

        Assert.All(depths, d => Assert.Equal(10, d));
        Assert.Equal(3, depths.Count);
    }

    [Fact]
    public async Task CancellationIsPropagated()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        RetrieveAsync retrieve = (_, _, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<RankedHit>>([]); };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RetrievalEvaluator.EvaluateAsync(Dataset, retrieve, new EvaluationOptions(), null, cts.Token));
    }

    [Fact]
    public void LatencyStats_UseNearestRankPercentiles()
    {
        var stats = LatencyStats.From([10, 20, 30, 40, 50, 60, 70, 80, 90, 100]);

        Assert.Equal(55.0, stats.MeanMs, Tolerance);
        Assert.Equal(50.0, stats.P50Ms, Tolerance);   // ceil(0.5 * 10) = 5th value
        Assert.Equal(100.0, stats.P95Ms, Tolerance);  // ceil(0.95 * 10) = 10th value
        Assert.Equal(100.0, stats.MaxMs, Tolerance);
    }
}
