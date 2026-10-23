using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rag.Core.Reranking;
using Rag.Reranking.Onnx;

namespace Rag.Reranking.Onnx.Tests;

/// <summary>
/// Real inference against the downloaded ms-marco-MiniLM-L6-v2 ONNX model. Expected ids and logits come from an independent
/// reference (Hugging Face tokenizers + Python onnxruntime, same fp32 file), captured in Fixtures/reference.json.
/// Skipped, not failed, when the model has not been downloaded.
/// </summary>
public sealed class RealModelTests : IDisposable
{
    private readonly Lazy<OnnxCrossEncoderReranker> _reranker = new(() => new OnnxCrossEncoderReranker(
        Microsoft.Extensions.Options.Options.Create(new OnnxRerankerOptions
        {
            ModelPath = ModelFiles.ModelPath,
            VocabPath = ModelFiles.VocabPath,
            BatchSize = 8,
            MaxConcurrentInferences = 2
        })));

    public void Dispose()
    {
        if (_reranker.IsValueCreated)
        {
            _reranker.Value.Dispose();
        }
    }

    [ModelFact]
    public void Tokenizer_ProducesExactlyTheReferenceIdsAndSegmentIds_IncludingTruncation()
    {
        var tokenizer = new CrossEncoderTokenizer(ModelFiles.VocabPath, 512);

        foreach (var pair in Reference.Pairs)
        {
            var actual = tokenizer.Encode(pair.Query, pair.Document);

            Assert.True(pair.InputIds.SequenceEqual(actual.InputIds), $"input ids differ for '{pair.Label}'");
            Assert.True(pair.TokenTypeIds.SequenceEqual(actual.TokenTypeIds), $"segment ids differ for '{pair.Label}'");
        }

        Assert.Contains(Reference.Pairs, p => p.InputIds.Length == 512);   // the long-document pair really exercised truncation
    }

    [ModelFact]
    public async Task Logits_MatchThePythonOnnxRuntimeReference()
    {
        foreach (var pair in Reference.Pairs)
        {
            var score = (await _reranker.Value.RerankAsync(pair.Query, [new RerankCandidate("c", pair.Document)], CancellationToken.None)).Single();

            Assert.True(Math.Abs(score.Score - pair.Logit) < 1e-3, $"'{pair.Label}': {score.Score} vs reference {pair.Logit}");
        }
    }

    [ModelFact]
    public async Task Padding_DoesNotChangeAPairsScore_WhenScoredInsideAMixedLengthBatch()
    {
        // The batch pads short pairs up to the 512-token pair; the attention mask must make that invisible.
        var query = Reference.Pairs[0].Query;
        var candidates = Reference.Pairs.Where(p => p.Query == query || p.Label is "long-document" or "unicode" or "punctuation")
            .Select(p => new RerankCandidate(p.Label, p.Document)).ToArray();

        var batched = (await _reranker.Value.RerankAsync(query, candidates, CancellationToken.None)).ToDictionary(s => s.ChunkId, s => s.Score);

        foreach (var candidate in candidates)
        {
            var alone = (await _reranker.Value.RerankAsync(query, [candidate], CancellationToken.None)).Single().Score;
            Assert.True(Math.Abs(alone - batched[candidate.ChunkId]) < 1e-3, $"'{candidate.ChunkId}': alone {alone} vs batched {batched[candidate.ChunkId]}");
        }
    }

    [ModelFact]
    public async Task EveryRelevantDocument_OutscoresItsIrrelevantCounterpart()
    {
        foreach (var queryGroup in Reference.Pairs.Where(p => p.Label.Contains('/')).GroupBy(p => p.Label.Split('/')[0]))
        {
            var relevant = queryGroup.Single(p => p.Label.EndsWith("/relevant"));
            var irrelevant = queryGroup.Single(p => p.Label.EndsWith("/irrelevant"));

            var scores = (await _reranker.Value.RerankAsync(relevant.Query,
                [new RerankCandidate("irrelevant", irrelevant.Document), new RerankCandidate("relevant", relevant.Document)],
                CancellationToken.None)).ToDictionary(s => s.ChunkId, s => s.Score);

            Assert.True(scores["relevant"] > scores["irrelevant"], queryGroup.Key);
        }
    }

    [ModelFact]
    public async Task ConcurrentCallsOnTheSharedSession_ReturnTheSameScoresAsASequentialCall()
    {
        var pair = Reference.Pairs[0];
        var candidates = Reference.Pairs.Select(p => new RerankCandidate(p.Label, p.Document)).ToArray();
        var expected = (await _reranker.Value.RerankAsync(pair.Query, candidates, CancellationToken.None)).ToDictionary(s => s.ChunkId, s => s.Score);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _reranker.Value.RerankAsync(pair.Query, candidates, CancellationToken.None)));

        foreach (var scores in results)
        {
            foreach (var score in scores)
            {
                Assert.True(Math.Abs(score.Score - expected[score.ChunkId]) < 1e-4, score.ChunkId);
            }
        }
    }

    [ModelFact]
    public async Task CancellingARunningInference_AbortsTheCall()
    {
        var candidates = Enumerable.Range(0, 64).Select(i => new RerankCandidate($"c{i}", Reference.Pairs.Last().Document)).ToArray();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _reranker.Value.RerankAsync(Reference.Pairs[0].Query, candidates, cts.Token));
    }

    [ModelFact]
    public void DependencyInjection_RegistersOneSharedRerankerWhenEnabled()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Rerank:Enabled"] = "true",
            ["OnnxReranker:ModelPath"] = ModelFiles.ModelPath,
            ["OnnxReranker:VocabPath"] = ModelFiles.VocabPath
        }).Build();

        using var provider = new ServiceCollection().AddOnnxReranker(configuration).BuildServiceProvider();

        var first = provider.GetRequiredService<IReranker>();
        Assert.Same(first, provider.GetRequiredService<IReranker>());
        Assert.Equal("onnx:ms-marco-MiniLM-L6-v2:fp32", first.Name);
    }

    [Fact]
    public void DependencyInjection_RegistersNothing_AndLoadsNoModel_WhenDisabled()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OnnxReranker:ModelPath"] = "/definitely/not/there.onnx"
        }).Build();

        using var provider = new ServiceCollection().AddOnnxReranker(configuration).BuildServiceProvider();

        Assert.Null(provider.GetService<IReranker>());
    }

    [Fact]
    public void MissingModelFile_FailsWithAnActionableMessage()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => new OnnxCrossEncoderRuntime("/definitely/not/there.onnx", 0));

        Assert.Contains("download-reranker-model.sh", ex.Message);
    }
}
