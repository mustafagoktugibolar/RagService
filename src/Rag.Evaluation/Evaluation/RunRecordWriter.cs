using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Rag.Evaluation.Evaluation;

public static class RunRecordWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string ToJson(RunRecord run) => JsonSerializer.Serialize(run, JsonOptions);

    public static string ToMarkdown(RunRecord run)
    {
        var report = run.Report;
        var builder = new StringBuilder();
        builder.AppendLine($"# Retrieval evaluation — {run.Label}");
        builder.AppendLine();
        builder.AppendLine("| Setting | Value |");
        builder.AppendLine("|---|---|");
        builder.AppendLine($"| Strategy | `{run.Strategy}` |");
        builder.AppendLine($"| Embedder | `{run.Embedder}` ({run.EmbedderKind}) |");
        builder.AppendLine($"| Chunking | size {run.Chunking.ChunkSize} / overlap {run.Chunking.ChunkOverlap} tokens |");
        builder.AppendLine($"| Reranker | `{run.Reranker}` |");
        builder.AppendLine($"| Query transformer | `{run.QueryTransformer}` |");
        builder.AppendLine($"| Dataset version | `{run.DatasetVersion}` ({report.QueryCount} queries) |");
        builder.AppendLine($"| Index version | `{run.IndexVersion}` |");
        builder.AppendLine($"| Git commit | `{run.GitCommit ?? "unknown"}` |");
        builder.AppendLine();

        AppendLevel(builder, "Document level", report.Document);
        if (report.Chunk is not null)
        {
            AppendLevel(builder, "Chunk level", report.Chunk);
        }

        builder.AppendLine("## Latency (retrieval only, per query)");
        builder.AppendLine();
        builder.AppendLine("| mean ms | p50 ms | p95 ms | max ms |");
        builder.AppendLine("|---:|---:|---:|---:|");
        builder.AppendLine($"| {F(report.Latency.MeanMs, 2)} | {F(report.Latency.P50Ms, 2)} | {F(report.Latency.P95Ms, 2)} | {F(report.Latency.MaxMs, 2)} |");
        builder.AppendLine();
        return builder.ToString();
    }

    private static void AppendLevel(StringBuilder builder, string title, LevelReport level)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine($"Scored queries: {level.ScoredQueries}; skipped (no relevant item at this level): {level.SkippedQueries}.");
        builder.AppendLine();
        builder.AppendLine("| K | Hit | Precision | Recall | MRR | nDCG | MAP |");
        builder.AppendLine("|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var (k, m) in level.ByK.OrderBy(p => p.Key))
        {
            builder.AppendLine($"| {k} | {F(m.Hit)} | {F(m.Precision)} | {F(m.Recall)} | {F(m.Mrr)} | {F(m.Ndcg)} | {F(m.Map)} |");
        }

        builder.AppendLine();
        if (level.ByCategory.Count == 0)
        {
            return;
        }

        var maxK = level.ByK.Keys.Max();
        builder.AppendLine($"By query category at K={maxK}:");
        builder.AppendLine();
        builder.AppendLine("| Category | Queries | Hit | Recall | MRR | nDCG |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var (category, report) in level.ByCategory.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var m = report.ByK[maxK];
            builder.AppendLine($"| {category} | {report.ScoredQueries} | {F(m.Hit)} | {F(m.Recall)} | {F(m.Mrr)} | {F(m.Ndcg)} |");
        }

        builder.AppendLine();
    }

    private static string F(double value, int digits = 3) =>
        value.ToString("F" + digits, CultureInfo.InvariantCulture);
}
