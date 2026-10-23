using Rag.Core.Reranking;
using Rag.Reranking.Onnx;

namespace Rag.Reranking.Onnx.Tests;

public sealed class OnnxCrossEncoderRerankerTests
{
    private static OnnxCrossEncoderReranker Build(FakeRuntime runtime, int batchSize = 16, int concurrency = 1, int maxLength = 512) =>
        new(new OnnxRerankerOptions { BatchSize = batchSize, MaxConcurrentInferences = concurrency, MaxSequenceLength = maxLength, ModelId = "test:fp32" },
            new CrossEncoderTokenizer(TinyVocab.Create(), maxLength), runtime);

    private static RerankCandidate C(string id, string text) => new(id, text);

    private static readonly RerankCandidate[] Mixed =
    [
        C("short", "queue"),
        C("long", "dead letter exchange queue retry dead letter exchange"),
        C("mid", "dead letter queue"),
        C("tiny", "a")
    ];

    [Fact]
    public void Name_CarriesTheModelIdentity() =>
        Assert.Equal("onnx:test:fp32", Build(new FakeRuntime()).Name);

    [Fact]
    public async Task ScoresAreReturnedByChunkId_NotByInternalBatchOrder()
    {
        var runtime = new FakeRuntime();
        var reranker = Build(runtime, batchSize: 2);

        var scores = await reranker.RerankAsync("dead", Mixed, CancellationToken.None);

        Assert.Equal(Mixed.Select(c => c.ChunkId), scores.Select(s => s.ChunkId));   // same order and identity as the input
        // The fake scores a row by the sum of its ids, so a longer text must outscore a shorter one containing the same words.
        var byId = scores.ToDictionary(s => s.ChunkId, s => s.Score);
        Assert.True(byId["long"] > byId["mid"] && byId["mid"] > byId["tiny"]);
    }

    [Fact]
    public async Task EachCandidateGetsTheScoreOfItsOwnRow_EvenWhenBatchingReordersThem()
    {
        var runtime = new FakeRuntime();
        var reranker = Build(runtime, batchSize: 3);
        var tokenizer = new CrossEncoderTokenizer(TinyVocab.Create(), 512);

        var scores = await reranker.RerankAsync("dead", Mixed, CancellationToken.None);

        foreach (var (candidate, score) in Mixed.Zip(scores))
        {
            var single = PaddedBatch.From([tokenizer.Encode("dead", candidate.Text)]);
            Assert.Equal(FakeRuntime.Expected(single, 0), score.Score);
        }
    }

    [Fact]
    public async Task Candidates_AreSplitIntoBatchesOfTheConfiguredSize()
    {
        var runtime = new FakeRuntime();
        var candidates = Enumerable.Range(0, 7).Select(i => C($"c{i}", "queue retry")).ToArray();

        await Build(runtime, batchSize: 3).RerankAsync("dead", candidates, CancellationToken.None);

        Assert.Equal([3, 3, 1], runtime.Batches.Select(b => b.Rows).ToArray());
    }

    [Fact]
    public async Task Padding_UsesZeroIdsAndZeroMask_AndMaskCountMatchesEachPairLength()
    {
        var runtime = new FakeRuntime();
        var tokenizer = new CrossEncoderTokenizer(TinyVocab.Create(), 512);

        await Build(runtime, batchSize: 16).RerankAsync("dead", Mixed, CancellationToken.None);

        var batch = Assert.Single(runtime.Batches);
        var lengths = Mixed.Select(c => tokenizer.Encode("dead", c.Text).Length).OrderByDescending(l => l).ToArray();
        Assert.Equal(lengths[0], batch.Length);                       // padded to the longest pair
        for (var row = 0; row < batch.Rows; row++)
        {
            var mask = batch.AttentionMask.Skip(row * batch.Length).Take(batch.Length).ToArray();
            Assert.Equal(lengths[row], mask.Sum());                  // rows are length-sorted, descending
            for (var column = (int)mask.Sum(); column < batch.Length; column++)
            {
                Assert.Equal(0, batch.InputIds[row * batch.Length + column]);
                Assert.Equal(0, batch.TokenTypeIds[row * batch.Length + column]);
            }
        }
    }

    [Fact]
    public async Task Batches_GroupSimilarLengths_SoTheLongestPairsShareABatch()
    {
        var runtime = new FakeRuntime();
        var candidates = new[]
        {
            C("a", "queue"), C("b", "dead letter exchange queue retry dead letter"), C("c", "queue retry"),
            C("d", "dead letter exchange queue retry dead letter exchange"), C("e", "a")
        };

        await Build(runtime, batchSize: 2).RerankAsync("dead", candidates, CancellationToken.None);

        var lengths = runtime.Batches.Select(b => b.Length).ToArray();
        Assert.Equal(lengths.OrderByDescending(l => l).ToArray(), lengths);   // first batch holds the longest pairs
    }

    [Fact]
    public async Task NoCandidates_ReturnsNothingAndNeverCallsTheRuntime()
    {
        var runtime = new FakeRuntime();

        Assert.Empty(await Build(runtime).RerankAsync("dead", [], CancellationToken.None));
        Assert.Empty(runtime.Batches);
    }

    [Fact]
    public async Task ConcurrentCalls_NeverExceedTheConfiguredInferenceLimit()
    {
        var runtime = new FakeRuntime { Work = TimeSpan.FromMilliseconds(40) };
        var reranker = Build(runtime, batchSize: 1, concurrency: 2);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => reranker.RerankAsync("dead", Mixed, CancellationToken.None)));

        Assert.Equal(2, runtime.MaxConcurrent);
        Assert.Equal(6 * Mixed.Length, runtime.Batches.Count);
    }

    [Fact]
    public async Task AlreadyCancelled_ThrowsBeforeAnyInference()
    {
        var runtime = new FakeRuntime();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(runtime).RerankAsync("dead", Mixed, cts.Token));
        Assert.Empty(runtime.Batches);
    }

    [Fact]
    public async Task CancelledBetweenBatches_StopsWithoutRunningTheRest()
    {
        using var cts = new CancellationTokenSource();
        var runtime = new FakeRuntime { OnScore = _ => cts.Cancel() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Build(runtime, batchSize: 1).RerankAsync("dead", Mixed, cts.Token));

        Assert.Single(runtime.Batches);
    }

    [Fact]
    public async Task WaitingForAnInferenceSlot_CanBeCancelled()
    {
        var runtime = new FakeRuntime { Work = TimeSpan.FromMilliseconds(300) };
        var reranker = Build(runtime, batchSize: 4, concurrency: 1);
        var running = reranker.RerankAsync("dead", Mixed, CancellationToken.None);
        await Task.Delay(50);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reranker.RerankAsync("dead", Mixed, cts.Token));
        await running;
        Assert.Single(runtime.Batches);   // the cancelled call never reached the runtime
    }

    [Fact]
    public async Task Dispose_ReleasesTheRuntime_RejectsFurtherUse_AndIsIdempotent()
    {
        var runtime = new FakeRuntime();
        var reranker = Build(runtime);

        reranker.Dispose();
        reranker.Dispose();

        Assert.True(runtime.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reranker.RerankAsync("dead", Mixed, CancellationToken.None));
    }

    [Fact]
    public async Task RuntimeReturningTheWrongNumberOfScores_IsReportedNotSilentlyMisassigned()
    {
        var runtime = new FakeRuntime { ScoreCountOverride = rows => rows - 1 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Build(runtime).RerankAsync("dead", Mixed, CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void InvalidBatchOrConcurrencySettings_AreRejected(int batch, int concurrency) =>
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => Build(new FakeRuntime(), batch, concurrency));
}
