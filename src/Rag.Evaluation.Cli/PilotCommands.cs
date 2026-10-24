using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Rag.Core.Options;
using Rag.Core.Reranking;
using Rag.Evaluation.Dataset;
using Rag.Evaluation.Harness;
using Rag.Evaluation.Human;
using Rag.Reranking.Onnx;

namespace Rag.Evaluation.Cli;

/// <summary>
/// Offline preparation and analysis for the human-labeling pilot. No mode here reads credentials, builds an embedder, or calls any
/// provider; the only model that can run is the optional local ONNX cross-encoder, used to add a candidate source to the pool.
/// </summary>
public static class PilotCommands
{
    public const string ConfirmationPhrase = "I reviewed the corpus and it is safe to send to external model providers";

    /// <summary>Explicit allow-list: nothing else is ever frozen into the pilot corpus.</summary>
    private static readonly (string Path, string Reason)[] AllowList =
    [
        ("README.md", "project overview"),
        ("docs/ASSESSMENT.md", "architecture assessment"),
        ("docs/kb/README.md", "knowledge base index"),
        ("docs/kb/01-system-architecture.md", "technical documentation (Turkish)"),
        ("docs/kb/06-advanced-retrieval.md", "technical documentation (Turkish)"),
        ("docs/kb/06b-reranking.md", "technical documentation (Turkish)"),
        ("docs/kb/07-retrieval-evaluation.md", "technical documentation (Turkish)"),
        ("docs/kb/08-redis-caching.md", "technical documentation (Turkish)"),
        ("docs/adr/0001-hybrid-search-not-default.md", "architecture decision record"),
        ("docs/adr/0002-index-versioned-query-cache.md", "architecture decision record"),
        ("docs/adr/0003-reranking-provider-abstraction.md", "architecture decision record"),
        ("docs/research/reranking-providers.md", "provider research notes")
    ];

    /// <summary>Neutral, hand-written areas shown to query writers instead of document titles (titles often state a conclusion).</summary>
    private static readonly Dictionary<string, string> TopicPrompts = new()
    {
        ["README.md"] = "How the system is built, what services it consists of and how it is run",
        ["docs/ASSESSMENT.md"] = "Strengths, weaknesses and risks found in the current code base",
        ["docs/kb/README.md"] = "How the learning material about this system is organised",
        ["docs/kb/01-system-architecture.md"] = "The overall architecture: query and indexing paths, workers, messaging",
        ["docs/kb/06-advanced-retrieval.md"] = "Retrieval techniques beyond plain vector search",
        ["docs/kb/06b-reranking.md"] = "Re-ordering retrieved results with a second stage: providers, safety, failure handling, measured results",
        ["docs/kb/07-retrieval-evaluation.md"] = "Measuring retrieval quality: metrics, datasets, tuning discipline, human labeling",
        ["docs/kb/08-redis-caching.md"] = "Caching of embeddings and of query results, and keeping cached answers correct",
        ["docs/adr/0001-hybrid-search-not-default.md"] = "A recorded decision about which search strategy is the default",
        ["docs/adr/0002-index-versioned-query-cache.md"] = "A recorded decision about cache correctness after re-indexing",
        ["docs/adr/0003-reranking-provider-abstraction.md"] = "A recorded decision about making ranking providers interchangeable",
        ["docs/research/reranking-providers.md"] = "What was found out about two external ranking services"
    };

    private static readonly ExcludedFile[] Excluded =
    [
        new("plan.md", "company-specific Definition Seeder plan; may carry proprietary names and domain details"),
        new(".env / .env.template", "credentials and default passwords"),
        new("docs/PLAN.md", "living document that changes with the work under evaluation"),
        new("docs/eval/**", "generated reports and protocols about the experiments themselves: circular, and they change"),
        new("datasets/**", "evaluation data"),
        new("src/** and tests/**", "source code is out of scope for this pilot (a possible later growth of the corpus)"),
        new("compose*.yaml, infra/**, Dockerfiles", "deployment files with default credentials"),
        new("models/**", "downloaded model files")
    ];

    public static async Task<int> RunAsync(string mode, Dictionary<string, string> options, CancellationToken ct)
    {
        var directory = options.GetValueOrDefault("pilot-dir") ?? Path.Combine("datasets", "human-pilot-v1");
        var workflow = new PilotWorkflow(directory);
        Console.WriteLine("Pilot step (offline; no paid call is made, no credential is read).");

        try
        {
            switch (mode)
            {
                case "pilot-corpus":
                    return await FreezeAsync(workflow, options, ct);

                case "pilot-signoff":
                    if (options.GetValueOrDefault("confirm") != ConfirmationPhrase || string.IsNullOrWhiteSpace(options.GetValueOrDefault("by")))
                    {
                        Console.Error.WriteLine($"REFUSING: review the corpus and the manifest's safety section, then pass --by <your-id> --confirm \"{ConfirmationPhrase}\".");
                        return 3;
                    }

                    var signed = workflow.SignOff(options["by"], DateTimeOffset.UtcNow);
                    Console.WriteLine($"Signed off by {options["by"]} (bound to manifest hash {signed.Safety.OwnerSignoff!.ManifestSha256[..12]}…).");
                    return 0;

                case "pilot-queries":
                    var queries = workflow.ImportQueries(File.ReadAllText(options.GetValueOrDefault("csv") ?? workflow.Path_("queries.csv")), workflow.ReadAnnotators());
                    Console.WriteLine($"{queries.Count} queries accepted -> queries.json");
                    foreach (var group in queries.GroupBy(q => q.Category))
                    {
                        Console.WriteLine($"  {group.Key,-15} {group.Count()}");
                    }

                    return 0;

                case "pilot-pool":
                    return await PoolAsync(workflow, options, ct);

                case "pilot-adjudication":
                    Console.WriteLine($"Disagreement sheet for the adjudicator: {workflow.WriteDisagreementSheet()}");
                    return 0;

                case "pilot-report":
                    return await ReportAsync(workflow, options, ct);

                case "pilot-approval-report":
                    return await ApprovalAsync(workflow, options, ct);

                case "pilot-coverage-proxy":
                    return await CoverageAsync(workflow, options, ct);

                default:
                    Console.Error.WriteLine($"Unknown pilot mode '{mode}'.");
                    return 2;
            }
        }
        catch (PilotValidationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (CorpusSafetyException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 4;
        }
    }

    private static async Task<int> FreezeAsync(PilotWorkflow workflow, Dictionary<string, string> options, CancellationToken ct)
    {
        var dirty = Git(new[] { "status", "--porcelain", "--" }.Concat(AllowList.Select(a => a.Path)).ToArray());
        if (!string.IsNullOrWhiteSpace(dirty) && !options.ContainsKey("allow-dirty"))
        {
            Console.Error.WriteLine("REFUSING: some allow-listed files have uncommitted changes. The manifest records a source commit, so commit them first:");
            Console.Error.WriteLine(dirty);
            return 3;
        }

        var spec = new ChunkingSpec(
            int.Parse(options.GetValueOrDefault("chunk-size") ?? "250"), int.Parse(options.GetValueOrDefault("chunk-overlap") ?? "40"), "text-embedding-3-small");
        var result = await workflow.FreezeCorpusAsync(Directory.GetCurrentDirectory(), AllowList, Excluded, spec, "human-pilot-corpus-v1", Git("rev-parse", "HEAD").Trim(), ct, TopicPrompts);

        var tokens = result.Chunks.Select(c => c.Text.Length / 4).Order().ToArray();
        Console.WriteLine($"Frozen {result.Manifest.Files.Count} documents into {result.Chunks.Count} chunks (chunking {spec.ChunkSize}/{spec.ChunkOverlap}); safety scan clean.");
        Console.WriteLine($"  languages: {string.Join(", ", result.Chunks.GroupBy(c => c.Language).Select(g => $"{g.Key} {g.Count()}"))}");
        Console.WriteLine($"  chunk size (~tokens): median {tokens[tokens.Length / 2]}, max {tokens[^1]}");
        Console.WriteLine($"  a top-20 candidate list is {20.0 / result.Chunks.Count:P0} of this corpus (golden-v1: 57 %)");
        Console.WriteLine("Next: review the manifest, then run pilot-signoff.");
        return 0;
    }

    private static async Task<int> PoolAsync(PilotWorkflow workflow, Dictionary<string, string> options, CancellationToken ct)
    {
        var extra = new List<PoolSystem>();
        var modelDirectory = options.GetValueOrDefault("model-dir") ?? Path.Combine("models", "ms-marco-MiniLM-L6-v2");
        OnnxCrossEncoderReranker? onnx = null;
        if (File.Exists(Path.Combine(modelDirectory, "model.onnx")))
        {
            var chunks = workflow.ReadChunks().ToDictionary(c => c.ChunkId);
            var bm25 = PoolBuilder.Bm25(chunks.Values.ToList());
            onnx = new OnnxCrossEncoderReranker(Microsoft.Extensions.Options.Options.Create(new OnnxRerankerOptions
            {
                ModelPath = Path.Combine(modelDirectory, "model.onnx"), VocabPath = Path.Combine(modelDirectory, "vocab.txt")
            }));
            extra.Add(new PoolSystem("bm25+onnx-rerank", (query, depth) =>
            {
                var candidates = bm25.Search(query, 30).Select(id => new RerankCandidate(id, chunks[id].Text)).ToList();
                return onnx.RerankAsync(query, candidates, ct).GetAwaiter().GetResult()
                    .OrderByDescending(s => s.Score).ThenBy(s => s.ChunkId, StringComparer.Ordinal).Take(depth).Select(s => s.ChunkId).ToList();
            }));
        }
        else
        {
            Console.WriteLine("Note: local ONNX model not found; pooling without the cross-encoder source (run scripts/download-reranker-model.sh to add it).");
        }

        using (onnx)
        {
            var golden = await DatasetLoader.LoadAsync(Path.Combine("datasets", "golden-v1", "dataset.json"), ct);
            var (pool, sheets) = workflow.BuildPools(extra, PilotWorkflow.GoldenQueries(golden),
                int.Parse(options.GetValueOrDefault("depth") ?? "10"), int.Parse(options.GetValueOrDefault("negatives") ?? "3"));

            Console.WriteLine($"Systems pooled: {string.Join(", ", pool.Systems)} (+ random negatives).");
            Console.WriteLine($"Median unique candidates per query: {pool.Pools.Values.Select(p => p.Count).Order().ElementAt(pool.Pools.Count / 2)}");
            foreach (var sheet in sheets)
            {
                Console.WriteLine($"Blinded sheet: {sheet}");
            }

            Console.WriteLine("Give each annotator ONLY their own sheet. pools/private/ holds the unblinded key: do not open it until all annotation is finished.");
        }

        return 0;
    }

    private static async Task<int> ReportAsync(PilotWorkflow workflow, Dictionary<string, string> options, CancellationToken ct)
    {
        var golden = await DatasetLoader.LoadAsync(Path.Combine("datasets", "golden-v1", "dataset.json"), ct);
        var (data, _, markdown) = workflow.BuildReport(PilotWorkflow.GoldenQueries(golden));
        var path = options.GetValueOrDefault("out") ?? Path.Combine("docs", "eval", "human-pilot-report.md");
        await File.WriteAllTextAsync(path, markdown, ct);
        Console.WriteLine($"{data.Decision}\nReport written to {path}");
        return 0;
    }

    private static string Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static async Task<int> ApprovalAsync(PilotWorkflow workflow, Dictionary<string, string> options, CancellationToken ct)
    {
        var manifest = workflow.ReadManifest();
        var chunks = workflow.ReadChunks();
        var documents = manifest.Files.ToDictionary(f => f.Path, f => File.ReadAllText(workflow.Path_("corpus", f.DocumentId + ".md")));

        var personal = new List<string>();
        personal.AddRange(Git("config", "user.name").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var owner = System.Text.RegularExpressions.Regex.Match(Git("remote", "get-url", "origin"), @"github\.com[:/]([^/]+)/").Groups[1].Value;
        if (owner.Length > 0)
        {
            personal.Add(owner);
        }

        var branch = options.GetValueOrDefault("branch") ?? "origin/feat/retrieval-engineering";
        var provenance = manifest.Files.Select(f =>
        {
            var log = Git("log", "--follow", "--format=%an", "--", f.Path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var messages = Git("log", "--follow", "--format=%B", "--", f.Path);
            var onMain = GitOk("cat-file", "-e", $"origin/main:{f.Path}");
            bool? identical = null;
            if (GitOk("cat-file", "-e", $"{branch}:{f.Path}"))
            {
                identical = Git("rev-parse", $"{branch}:{f.Path}").Trim() == Git("hash-object", workflow.Path_("corpus", f.DocumentId + ".md")).Trim();
            }

            return new DocumentProvenance(f.DocumentId, f.Path, log.Length, log.Distinct().Order().ToList(),
                messages.Contains("Co-Authored-By: Claude", StringComparison.OrdinalIgnoreCase), onMain, identical);
        }).ToList();

        var cacheDirectory = options.GetValueOrDefault("cache") ?? ".eval-cache";
        var cached = Directory.Exists(cacheDirectory) ? Directory.GetFiles(cacheDirectory, "*.bin").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        var cache = new EmbeddingCacheCheck(chunks.Count,
            chunks.Count(c => cached.Contains(FileCachingEmbeddingProvider.KeyFor("text-embedding-3-small", c.Text))), cached.Count,
            chunks.Sum(c => (long)c.Text.Length / 4));

        var notesPath = workflow.Path_("review-notes.md");
        string? notes = null;
        var notesHash = string.Empty;
        if (File.Exists(notesPath))
        {
            var lines = File.ReadAllLines(notesPath);
            notesHash = lines.FirstOrDefault(l => l.StartsWith("manifest-hash:", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            notes = string.Join('\n', lines.Where(l => !l.StartsWith("manifest-hash:", StringComparison.OrdinalIgnoreCase)));
        }

        var report = CorpusApproval.Build(manifest, chunks, provenance, AdvisoryReview.Scan(documents, personal), cache, notes, notesHash);
        var path = options.GetValueOrDefault("out") ?? Path.Combine("docs", "eval", "corpus-approval-report.md");
        await File.WriteAllTextAsync(path, report, ct);
        Console.WriteLine($"Corpus approval report written to {path} (signed off: {(CorpusBuilder.IsSignedOff(manifest) ? "yes" : "NO")}).");
        return 0;
    }

    private static (PoolSystem System, IDisposable Disposable)? OnnxSystem(IReadOnlyList<PilotChunk> chunks, string modelDirectory, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(modelDirectory, "model.onnx")))
        {
            return null;
        }

        var byId = chunks.ToDictionary(c => c.ChunkId);
        var bm25 = PoolBuilder.Bm25(chunks);
        var onnx = new OnnxCrossEncoderReranker(Microsoft.Extensions.Options.Options.Create(new OnnxRerankerOptions
        {
            ModelPath = Path.Combine(modelDirectory, "model.onnx"), VocabPath = Path.Combine(modelDirectory, "vocab.txt")
        }));
        return (new PoolSystem("bm25+onnx-rerank", (query, depth) =>
            onnx.RerankAsync(query, bm25.Search(query, 30).Select(id => new RerankCandidate(id, byId[id].Text)).ToList(), ct).GetAwaiter().GetResult()
                .OrderByDescending(x => x.Score).ThenBy(x => x.ChunkId, StringComparer.Ordinal).Take(depth).Select(x => x.ChunkId).ToList()), onnx);
    }

    /// <summary>
    /// Two free analyses. (1) On golden-v1, where REAL OpenAI embeddings are cached: how much of the real dense top-k do the free pooling systems
    /// capture? (2) On the pilot corpus, with synthetic proxy queries: how big will pools be and how much do the systems overlap?
    /// Uses a cache-only embedder: no credential is read and a live call is impossible.
    /// </summary>
    private static async Task<int> CoverageAsync(PilotWorkflow workflow, Dictionary<string, string> options, CancellationToken ct)
    {
        var modelDirectory = options.GetValueOrDefault("model-dir") ?? Path.Combine("models", "ms-marco-MiniLM-L6-v2");
        var report = new StringBuilder();
        report.AppendLine("# Candidate-pool coverage of the production dense pipeline");
        report.AppendLine();
        report.AppendLine("Generated by `--mode pilot-coverage-proxy`. Free and offline: it reads only embeddings already in the local cache (no credential, no network) and runs BM25, the hashing embedder and the local ONNX model.");
        report.AppendLine();

        // ---- Part 1: golden-v1 with real, cached embeddings
        var golden = await DatasetLoader.LoadAsync(Path.Combine("datasets", "golden-v1", "dataset.json"), ct);
        var spec = new EmbedderSpec(new CacheOnlyEmbeddingProvider("text-embedding-3-small", options.GetValueOrDefault("cache") ?? ".eval-cache"),
            "OpenAI", "text-embedding-3-small", 1536, "real (cache only)");
        var harness = await DenseBaselineHarness.BuildAsync(golden, new HarnessConfiguration(), ct, spec);
        var goldenChunks = harness.ChunkCatalog.Select(c => new PilotChunk { ChunkId = c.ChunkId, DocumentId = c.DocumentId, Title = c.DocumentId, Language = "en", Text = c.Text }).ToList();
        var onnxGolden = OnnxSystem(goldenChunks, modelDirectory, ct);
        var systems = new List<PoolSystem> { PoolBuilder.Bm25(goldenChunks), PoolBuilder.HashingDense(goldenChunks) };
        if (onnxGolden is { } og)
        {
            systems.Add(og.System);
        }

        var realDense = harness.RetrieverFor(new RetrievalOptions { Strategy = RetrievalStrategy.Dense });
        var scored = golden.Queries.Where(q => q.RelevantDocuments.Count > 0).ToList();
        var rows = new List<(string Category, int K, Dictionary<string, double> Overlap, double UnionCoverage, double CoverageWithoutHashing, double UnionSize, double RelevantMissed, int RelevantInReal)>();
        foreach (var k in new[] { 5, 10 })
        {
            foreach (var query in scored)
            {
                var real = (await realDense(query, k, ct)).Select(h => h.ChunkId).ToList();
                var per = systems.ToDictionary(s => s.Name, s => (IReadOnlyList<string>)s.Search(query.Query, k));
                var union = per.Values.SelectMany(v => v).ToHashSet(StringComparer.Ordinal);
                var withoutHashing = per.Where(p => p.Key != "hashing-dense").SelectMany(p => p.Value).ToHashSet(StringComparer.Ordinal);
                var relevant = goldenChunks.Where(c => query.RelevantDocuments.Any(r => r.DocumentId == c.DocumentId)).Select(c => c.ChunkId).ToHashSet(StringComparer.Ordinal);
                var relevantInReal = real.Where(relevant.Contains).ToList();
                rows.Add((query.Category ?? "?", k, per.ToDictionary(p => p.Key, p => PoolCoverage.Overlap(real, p.Value, k)),
                    PoolCoverage.Coverage(real, union, k), PoolCoverage.Coverage(real, withoutHashing, k), union.Count,
                    relevantInReal.Count == 0 ? 0 : relevantInReal.Count(id => !union.Contains(id)) / (double)relevantInReal.Count, relevantInReal.Count));
            }
        }

        report.AppendLine("## 1. golden-v1 with the REAL `text-embedding-3-small` vectors (from the cache)");
        report.AppendLine();
        report.AppendLine($"{scored.Count} scored queries over a corpus of {goldenChunks.Count} chunks. Pool = union of the systems' top-k; the question is how much of the real dense top-k it already contains. " +
                          "Chance level is the pool size over the corpus size, so coverage must be read against it.");
        report.AppendLine();
        foreach (var k in new[] { 5, 10 })
        {
            var at = rows.Where(r => r.K == k).ToList();
            report.AppendLine($"### top-{k}");
            report.AppendLine();
            report.AppendLine($"| group | queries | " + string.Join(" | ", systems.Select(s => $"overlap with real dense: {s.Name}")) + " | pool coverage of real dense | coverage without hashing-dense | mean pool size | chance level | judged-relevant real-dense hits missed by pool |");
            report.AppendLine("|---|---:|" + string.Concat(systems.Select(_ => "---:|")) + "---:|---:|---:|---:|---:|");
            var groups = at.GroupBy(r => r.Category).OrderBy(g => g.Key).Select(g => new CoverageGroup(g.Key, g.ToList())).ToList();
            groups.Add(new CoverageGroup("**all**", at));
            foreach (var group in groups)
            {
                var meanSize = group.Rows.Average(r => r.UnionSize);
                report.AppendLine($"| {group.Name} | {group.Rows.Count} | " + string.Join(" | ", systems.Select(s => group.Rows.Average(r => r.Overlap[s.Name]).ToString("P0", CultureInfo.InvariantCulture))) +
                    $" | {group.Rows.Average(r => r.UnionCoverage):P0} | {group.Rows.Average(r => r.CoverageWithoutHashing):P0} | {meanSize:0.0} | {PoolCoverage.ChanceCoverage((int)Math.Round(meanSize), goldenChunks.Count):P0} | {group.Rows.Average(r => r.RelevantMissed):P0} |");
            }

            report.AppendLine();
        }

        report.AppendLine("**How to read it.** Hashing-dense is a bag-of-words embedder: on keyword-style queries it should resemble real dense, on paraphrases much less. The rows show how far that holds. " +
                          "**Caveat on this evidence:** golden-v1 is English, tiny (35 chunks) and its queries were written next to its passages, so lexical systems look better here than they will on the Turkish-heavy pilot corpus; top-10 pools of three systems nearly cover a 35-chunk corpus by construction, which is why top-5 is the informative row.");
        report.AppendLine();

        // ---- Part 2: simulation on the pilot corpus
        var chunks = workflow.ReadChunks();
        var onnxPilot = OnnxSystem(chunks, modelDirectory, ct);
        var pilotSystems = new List<PoolSystem> { PoolBuilder.Bm25(chunks), PoolBuilder.HashingDense(chunks) };
        if (onnxPilot is { } op)
        {
            pilotSystems.Add(op.System);
        }

        var proxies = ProxyQueries.Generate(chunks, 24, 4, 20261024);
        var languageOf = chunks.ToDictionary(c => c.ChunkId, c => c.Language);
        var sims = proxies.Select(q =>
        {
            var per = pilotSystems.ToDictionary(s => s.Name, s => (IReadOnlyList<string>)s.Search(q.Text, 10));
            return (Query: q, Language: languageOf[q.SourceChunkId], Rankings: per, Union: PoolCoverage.UnionSize(per, 10), Contribution: PoolCoverage.UniqueContribution(per, 10));
        }).ToList();

        report.AppendLine("## 2. Simulated pools on the pilot corpus (synthetic proxy queries)");
        report.AppendLine();
        report.AppendLine($"{sims.Count} proxy queries of 4 mid-frequency terms each, drawn from random passages (seeded). **They are keyword-style by construction**: they overstate the overlap between lexical systems and say nothing about paraphrase. They are never labeled, never shown to annotators and never enter a dataset; they only size the pools.");
        report.AppendLine();
        var sizes = sims.Select(s => s.Union).Order().ToList();
        var medianUnion = sizes[sizes.Count / 2];
        report.AppendLine($"Union of the systems' top-10: median **{medianUnion}**, min {sizes[0]}, max {sizes[^1]} passages per query (+3 random negatives in the real pool).");
        report.AppendLine();
        report.AppendLine("| system pair | mean Jaccard of top-10 | | system | mean passages only this system found |");
        report.AppendLine("|---|---:|---|---|---:|");
        var names = pilotSystems.Select(s => s.Name).ToList();
        var pairs = names.SelectMany((a, i) => names.Skip(i + 1).Select(b => (a, b))).ToList();
        for (var i = 0; i < Math.Max(pairs.Count, names.Count); i++)
        {
            var left = i < pairs.Count ? $"{pairs[i].a} vs {pairs[i].b} | {sims.Average(s => PoolCoverage.Jaccard(s.Rankings[pairs[i].a], s.Rankings[pairs[i].b], 10)):0.00}" : " | ";
            var right = i < names.Count ? $"{names[i]} | {sims.Average(s => s.Contribution[names[i]]):0.0}" : " | ";
            report.AppendLine($"| {left} | | {right} |");
        }

        report.AppendLine();
        report.AppendLine("| proxy-query source language | queries | median union | mean Jaccard bm25 vs hashing-dense |");
        report.AppendLine("|---|---:|---:|---:|");
        foreach (var group in sims.GroupBy(s => s.Language).OrderBy(g => g.Key))
        {
            var u = group.Select(s => s.Union).Order().ToList();
            report.AppendLine($"| {group.Key} | {group.Count()} | {u[u.Count / 2]} | {group.Average(s => PoolCoverage.Jaccard(s.Rankings["bm25"], s.Rankings["hashing-dense"], 10)):0.00} |");
        }

        report.AppendLine();
        var pooled = medianUnion + 3;
        report.AppendLine("### Expected human judgments (24 queries, 2 annotators)");
        report.AppendLine();
        report.AppendLine($"Median pool ≈ {pooled} passages per query (union {medianUnion} + 3 random) → 24 × {pooled} = **{24 * pooled} passages**, graded by two people = **≈ {24 * pooled * 2} judgments**.");
        report.AppendLine();
        report.AppendLine("| seconds per judgment | total annotator hours | per annotator |");
        report.AppendLine("|---:|---:|---:|");
        foreach (var seconds in new[] { 30, 45, 60 })
        {
            var hours = 24 * pooled * 2 * seconds / 3600.0;
            report.AppendLine($"| {seconds} | {hours:0.0} | {hours / 2:0.0} |");
        }

        report.AppendLine();
        report.AppendLine("Plus adjudication of every disagreement (any difference, any flag); at κ in the 0.6-0.8 range, plausibly a quarter to a third of the items. Adding a real-dense source would raise the pool by an amount this simulation cannot estimate (see the report text).");

        var path = options.GetValueOrDefault("out") ?? Path.Combine("docs", "eval", "pilot-pool-coverage.md");
        await File.WriteAllTextAsync(path, report.ToString(), ct);
        onnxGolden?.Disposable.Dispose();
        onnxPilot?.Disposable.Dispose();
        Console.WriteLine($"Coverage analysis written to {path}");
        return 0;
    }

    private sealed record CoverageGroup(string Name, IReadOnlyList<(string Category, int K, Dictionary<string, double> Overlap, double UnionCoverage, double CoverageWithoutHashing, double UnionSize, double RelevantMissed, int RelevantInReal)> Rows);

    private static bool GitOk(params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}
