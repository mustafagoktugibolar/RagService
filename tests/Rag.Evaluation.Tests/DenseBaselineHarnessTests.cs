using Rag.Evaluation.Dataset;
using Rag.Evaluation.Evaluation;
using Rag.Evaluation.Harness;

namespace Rag.Evaluation.Tests;

public sealed class DenseBaselineHarnessTests
{
    private static async Task<(EvaluationReport Report, int Chunks)> RunAsync(HarnessConfiguration? configuration = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "datasets", "golden-v1", "dataset.json");
        var dataset = await DatasetLoader.LoadAsync(path, CancellationToken.None);
        var harness = await DenseBaselineHarness.BuildAsync(dataset, configuration ?? new HarnessConfiguration(), CancellationToken.None);
        var report = await RetrievalEvaluator.EvaluateAsync(
            dataset, harness.Retriever, new EvaluationOptions(), harness.ChunkCatalog, CancellationToken.None);
        return (report, harness.ChunkCatalog.Count);
    }

    [Fact]
    public async Task TwoRunsOverTheSameDatasetProduceIdenticalRankingsAndMetrics()
    {
        var first = (await RunAsync()).Report;
        var second = (await RunAsync()).Report;

        Assert.Equal(first.Document.PerQuery.Count, second.Document.PerQuery.Count);
        for (var i = 0; i < first.Document.PerQuery.Count; i++)
        {
            Assert.Equal(first.Document.PerQuery[i].Ranked, second.Document.PerQuery[i].Ranked);
        }

        Assert.Equal(first.Document.ByK, second.Document.ByK);
        Assert.Equal(first.Chunk!.ByK, second.Chunk!.ByK);
    }

    [Fact]
    public async Task EveryCorpusDocumentIsIndexedAndNoAnswerQueriesAreSkippedNotScored()
    {
        var (report, chunks) = await RunAsync();

        Assert.True(chunks >= 35, "each of the 35 corpus documents yields at least one chunk");
        Assert.Equal(4, report.Document.SkippedQueries);   // the four no-answer queries
        Assert.Equal(40, report.Document.ScoredQueries);
    }

    [Fact]
    public async Task SmallerChunksProduceMoreChunks_ProvingTheChunkingConfigurationIsApplied()
    {
        var (_, defaultChunks) = await RunAsync();
        var (_, smallChunks) = await RunAsync(new HarnessConfiguration { ChunkSize = 40, ChunkOverlap = 8 });

        Assert.True(smallChunks > defaultChunks, $"{smallChunks} should exceed {defaultChunks}");
    }
}
