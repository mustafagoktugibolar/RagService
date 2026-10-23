using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Rag.Core.Reranking;
using Rag.Evaluation.Dataset;
using Rag.Reranking.Onnx;

namespace Rag.Evaluation.Cli;

/// <summary>
/// CPU latency and memory of the local cross-encoder on realistic input: 20 chunks from the golden corpus per query.
/// Numbers describe the machine they ran on (printed in the header) and are not portable guarantees.
/// </summary>
public static class RerankerBenchmark
{
    public static async Task<string> RunAsync(string modelDirectory, EvalDataset dataset, CancellationToken ct)
    {
        var texts = dataset.Corpus.Select(d => d.Text).ToArray();
        var candidates = Enumerable.Range(0, 20).Select(i => new RerankCandidate($"c{i}", texts[i % texts.Length])).ToArray();
        var queries = dataset.Queries.Select(q => q.Query).ToArray();

        var report = new StringBuilder();
        report.AppendLine("# Local cross-encoder CPU benchmark");
        report.AppendLine();
        report.AppendLine($"- Machine: {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} logical cores, .NET {Environment.Version}");
        report.AppendLine("- Input: 20 corpus chunks per query, 44 golden-v1.1.0 queries, 3 warm-up queries discarded");
        report.AppendLine("- Latency includes tokenisation + inference; excludes any network (there is none).");
        report.AppendLine();
        report.AppendLine("| model file | batch | intra-op threads | load ms | p50 ms | p95 ms | max ms |");
        report.AppendLine("|---|---:|---:|---:|---:|---:|---:|");

        foreach (var (file, label) in new[] { ("model.onnx", "fp32"), ("model_qint8_arm64.onnx", "qint8") })
        {
            if (!File.Exists(Path.Combine(modelDirectory, file)))
            {
                continue;
            }

            foreach (var (batch, threads) in new[] { (20, 0), (10, 0), (5, 0), (20, 4), (20, 1) })
            {
                var load = Stopwatch.StartNew();
                using var reranker = new OnnxCrossEncoderReranker(Microsoft.Extensions.Options.Options.Create(new OnnxRerankerOptions
                {
                    ModelPath = Path.Combine(modelDirectory, file),
                    VocabPath = Path.Combine(modelDirectory, "vocab.txt"),
                    ModelId = $"ms-marco-MiniLM-L6-v2:{label}",
                    BatchSize = batch,
                    IntraOpThreads = threads
                }));
                load.Stop();

                for (var warm = 0; warm < 3; warm++)
                {
                    await reranker.RerankAsync(queries[warm], candidates, ct);
                }

                var latencies = new List<double>();
                foreach (var query in queries)
                {
                    var sw = Stopwatch.StartNew();
                    await reranker.RerankAsync(query, candidates, ct);
                    latencies.Add(sw.Elapsed.TotalMilliseconds);
                }

                var sorted = latencies.Order().ToArray();
                var p95 = sorted[(int)Math.Ceiling(0.95 * sorted.Length) - 1];
                report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {label} | {batch} | {(threads == 0 ? "default" : threads.ToString())} | {load.Elapsed.TotalMilliseconds:F0} | {sorted[sorted.Length / 2 - 1]:F1} | {p95:F1} | {sorted[^1]:F1} |"));
            }
        }

        report.AppendLine();
        report.AppendLine("Memory is measured separately, one fresh process per model file (`--mode bench-memory`), because the working set of a process that loads several models in a row is not interpretable.");
        return report.ToString();
    }

    /// <summary>Working set of THIS process at three points: before the model, after loading it, after 20 reranks. Run once per fresh process.</summary>
    public static async Task<string> MemoryAsync(string modelDirectory, string modelFile, EvalDataset dataset, CancellationToken ct)
    {
        static double Mb() => Process.GetCurrentProcess().WorkingSet64 / 1048576.0;
        var texts = dataset.Corpus.Select(d => d.Text).ToArray();
        var candidates = Enumerable.Range(0, 20).Select(i => new RerankCandidate($"c{i}", texts[i % texts.Length])).ToArray();

        GC.Collect();
        var baseline = Mb();
        using var reranker = new OnnxCrossEncoderReranker(Microsoft.Extensions.Options.Options.Create(new OnnxRerankerOptions
        {
            ModelPath = Path.Combine(modelDirectory, modelFile),
            VocabPath = Path.Combine(modelDirectory, "vocab.txt"),
            BatchSize = 10
        }));
        var loaded = Mb();
        for (var i = 0; i < 20; i++)
        {
            await reranker.RerankAsync(dataset.Queries[i].Query, candidates, ct);
        }

        var used = Mb();
        return string.Create(CultureInfo.InvariantCulture,
            $"| {modelFile} | {baseline:F0} | {loaded:F0} (+{loaded - baseline:F0}) | {used:F0} (+{used - baseline:F0}) |");
    }
}
