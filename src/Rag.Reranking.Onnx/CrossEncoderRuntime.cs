using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Rag.Reranking.Onnx;

/// <summary>Right-padded batch in row-major layout [Rows × Length].</summary>
public sealed record PaddedBatch(long[] InputIds, long[] AttentionMask, long[] TokenTypeIds, int Rows, int Length)
{
    public static PaddedBatch From(IReadOnlyList<EncodedPair> pairs)
    {
        var length = pairs.Max(p => p.Length);
        var ids = new long[pairs.Count * length];
        var mask = new long[pairs.Count * length];
        var types = new long[pairs.Count * length];
        for (var row = 0; row < pairs.Count; row++)
        {
            for (var column = 0; column < pairs[row].Length; column++)
            {
                var offset = row * length + column;
                ids[offset] = pairs[row].InputIds[column];
                mask[offset] = 1;
                types[offset] = pairs[row].TokenTypeIds[column];
            }
        }

        return new PaddedBatch(ids, mask, types, pairs.Count, length);
    }
}

/// <summary>Runs the cross-encoder on a padded batch and returns one raw logit per row. Seam for deterministic tests.</summary>
public interface ICrossEncoderRuntime
{
    float[] Score(PaddedBatch batch, CancellationToken ct);
}

/// <summary>
/// ONNX Runtime CPU inference. The <see cref="InferenceSession"/> is created once and shared: <c>Run</c> is thread-safe,
/// and concurrency is bounded by the reranker, not here. Cancelling the token terminates the in-flight run.
/// </summary>
public sealed class OnnxCrossEncoderRuntime : ICrossEncoderRuntime, IDisposable
{
    private readonly InferenceSession _session;

    public OnnxCrossEncoderRuntime(string modelPath, int intraOpThreads)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"Cross-encoder model not found at '{modelPath}'. Run scripts/download-reranker-model.sh.", modelPath);
        }

        using var options = new SessionOptions();
        if (intraOpThreads > 0)
        {
            options.IntraOpNumThreads = intraOpThreads;
        }

        _session = new InferenceSession(modelPath, options);
    }

    public float[] Score(PaddedBatch batch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var runOptions = new RunOptions();
        using var registration = ct.Register(() => runOptions.Terminate = true);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(batch.InputIds, [batch.Rows, batch.Length])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(batch.AttentionMask, [batch.Rows, batch.Length])),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(batch.TokenTypeIds, [batch.Rows, batch.Length]))
        };

        try
        {
            using var results = _session.Run(inputs, _session.OutputNames, runOptions);
            return results.First().AsEnumerable<float>().ToArray();
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    public void Dispose() => _session.Dispose();
}
