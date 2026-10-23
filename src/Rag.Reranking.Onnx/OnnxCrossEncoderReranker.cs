using Microsoft.Extensions.Options;
using Rag.Core.Reranking;

namespace Rag.Reranking.Onnx;

/// <summary>
/// Local cross-encoder reranker: no network at runtime, CPU only. Scores are the model's raw logits (higher = more
/// relevant; not bounded or calibrated, so only their order within one call is meaningful).
/// <para>
/// Candidates are encoded, sorted by length so each batch pads as little as possible, scored in batches of
/// <c>BatchSize</c>, and mapped back to their chunk ids. At most <c>MaxConcurrentInferences</c> batches run at a time
/// across all callers; the CPU-bound call is moved off the caller's thread. Dispose releases the model.
/// </para>
/// </summary>
public sealed class OnnxCrossEncoderReranker : IReranker, IDisposable
{
    private readonly CrossEncoderTokenizer _tokenizer;
    private readonly ICrossEncoderRuntime _runtime;
    private readonly OnnxRerankerOptions _options;
    private readonly SemaphoreSlim _gate;
    private bool _disposed;

    /// <summary>Loads the model and vocabulary from the paths in <paramref name="options"/>. Create once; reuse for the process lifetime.</summary>
    public OnnxCrossEncoderReranker(IOptions<OnnxRerankerOptions> options)
        : this(
            options.Value,
            new CrossEncoderTokenizer(options.Value.VocabPath, options.Value.MaxSequenceLength),
            new OnnxCrossEncoderRuntime(options.Value.ModelPath, options.Value.IntraOpThreads))
    {
    }

    /// <summary>Seam for deterministic tests: any tokenizer/runtime pair.</summary>
    public OnnxCrossEncoderReranker(OnnxRerankerOptions options, CrossEncoderTokenizer tokenizer, ICrossEncoderRuntime runtime)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentInferences);
        _options = options;
        _tokenizer = tokenizer;
        _runtime = runtime;
        _gate = new SemaphoreSlim(options.MaxConcurrentInferences, options.MaxConcurrentInferences);
    }

    public string Name => $"onnx:{_options.ModelId}";

    public async Task<IReadOnlyList<RerankScore>> RerankAsync(
        string query,
        IReadOnlyList<RerankCandidate> candidates,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (candidates.Count == 0)
        {
            return [];
        }

        var encoded = candidates.Select(c => _tokenizer.Encode(query, c.Text)).ToArray();
        var order = Enumerable.Range(0, candidates.Count).OrderByDescending(i => encoded[i].Length).ThenBy(i => i).ToArray();
        var logits = new float[candidates.Count];

        foreach (var chunk in order.Chunk(_options.BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var batch = PaddedBatch.From(chunk.Select(i => encoded[i]).ToArray());

            await _gate.WaitAsync(ct);
            float[] scores;
            try
            {
                scores = await Task.Run(() => _runtime.Score(batch, ct), ct);
            }
            finally
            {
                _gate.Release();
            }

            if (scores.Length != chunk.Length)
            {
                throw new InvalidOperationException($"Runtime returned {scores.Length} scores for {chunk.Length} pairs.");
            }

            for (var k = 0; k < chunk.Length; k++)
            {
                logits[chunk[k]] = scores[k];
            }
        }

        return candidates.Select((c, i) => new RerankScore(c.ChunkId, logits[i])).ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        (_runtime as IDisposable)?.Dispose();
        _gate.Dispose();
    }
}
