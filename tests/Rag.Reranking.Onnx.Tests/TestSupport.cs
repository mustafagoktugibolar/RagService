using System.Text.Json;
using Rag.Reranking.Onnx;

namespace Rag.Reranking.Onnx.Tests;

/// <summary>Locates the downloaded model; tests that need it are skipped (not failed) when it is absent.</summary>
public static class ModelFiles
{
    public const string SkipReason = "Cross-encoder model not downloaded. Run scripts/download-reranker-model.sh.";

    public static string Directory { get; } = FindDirectory();

    public static string ModelPath => Path.Combine(Directory, "model.onnx");

    public static string VocabPath => Path.Combine(Directory, "vocab.txt");

    public static bool Exist => File.Exists(ModelPath) && File.Exists(VocabPath);

    private static string FindDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "models", "ms-marco-MiniLM-L6-v2");
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "models", "ms-marco-MiniLM-L6-v2");
    }
}

public sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute()
    {
        if (!ModelFiles.Exist)
        {
            Skip = ModelFiles.SkipReason;
        }
    }
}

public sealed record ReferencePair(string Label, string Query, string Document, int[] InputIds, int[] TokenTypeIds, double Logit);

public static class Reference
{
    public static IReadOnlyList<ReferencePair> Pairs { get; } = Load();

    private static List<ReferencePair> Load()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "reference.json"));
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("pairs").EnumerateArray().Select(p => new ReferencePair(
            p.GetProperty("label").GetString()!,
            p.GetProperty("query").GetString()!,
            p.GetProperty("document").GetString()!,
            p.GetProperty("input_ids").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
            p.GetProperty("token_type_ids").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
            p.GetProperty("logit").GetDouble())).ToList();
    }
}

/// <summary>A tiny WordPiece vocabulary so tokenizer behaviour can be tested without the real model files.</summary>
public static class TinyVocab
{
    public static string Create()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tiny-vocab-{Guid.NewGuid():N}.txt");
        var tokens = new List<string> { "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]" };
        tokens.AddRange("a b c d e f g h i j k l m n o p q r s t u v w x y z".Split(' '));
        tokens.AddRange(["dead", "letter", "exchange", "queue", "retry"]);
        File.WriteAllLines(path, tokens);
        return path;
    }

    public const int Cls = 2;
    public const int Sep = 3;
}

/// <summary>Deterministic runtime: the score of a row is the sum of its non-padding input ids. Records every call.</summary>
public sealed class FakeRuntime : ICrossEncoderRuntime, IDisposable
{
    private int _concurrent;

    public List<PaddedBatch> Batches { get; } = [];

    public int MaxConcurrent { get; private set; }

    public bool Disposed { get; private set; }

    public Action<PaddedBatch>? OnScore { get; init; }

    public TimeSpan Work { get; init; } = TimeSpan.Zero;

    public Func<int, int>? ScoreCountOverride { get; init; }

    public float[] Score(PaddedBatch batch, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _concurrent);
        lock (Batches)
        {
            Batches.Add(batch);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
        }

        try
        {
            OnScore?.Invoke(batch);
            if (Work > TimeSpan.Zero)
            {
                Thread.Sleep(Work);
            }

            var count = ScoreCountOverride?.Invoke(batch.Rows) ?? batch.Rows;
            return Enumerable.Range(0, count).Select(row => Expected(batch, row)).ToArray();
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }

    public static float Expected(PaddedBatch batch, int row)
    {
        float sum = 0;
        for (var column = 0; column < batch.Length; column++)
        {
            var offset = row * batch.Length + column;
            sum += batch.AttentionMask[offset] * batch.InputIds[offset];
        }

        return sum;
    }

    public void Dispose() => Disposed = true;
}
