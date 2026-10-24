using System.Diagnostics;
using Microsoft.Extensions.Options;
using Rag.Core.Reranking;
using Rag.Evaluation.Dataset;
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
}
