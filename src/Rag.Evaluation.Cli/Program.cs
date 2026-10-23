using System.Diagnostics;
using Microsoft.Extensions.Options;
using Rag.Core.Options;
using Rag.Core.Providers.Embeddings;
using Rag.Core.Retrieval;
using Rag.Evaluation.Dataset;
using Rag.Evaluation.Evaluation;
using Rag.Evaluation.Harness;
using Rag.Evaluation.Tuning;

// Usage: dotnet run --project src/Rag.Evaluation.Cli -- [--dataset <path>] [--out <dir>] [--label <name>]
//                                                        [--chunk-size <n>] [--chunk-overlap <n>]
//                                                        [--strategy dense|lexical|hybrid] [--candidates <n>] [--rrf-k <n>]
var options = ParseArgs(args);
var datasetPath = options.GetValueOrDefault("dataset") ?? Path.Combine("datasets", "golden-v1", "dataset.json");
var outDirectory = options.GetValueOrDefault("out") ?? Path.Combine("docs", "eval");
var label = options.GetValueOrDefault("label") ?? "baseline-dense";
var embedderName = options.GetValueOrDefault("embedder") ?? "hashing";

var configuration = new HarnessConfiguration
{
    ChunkSize = int.Parse(options.GetValueOrDefault("chunk-size") ?? "1000"),
    ChunkOverlap = int.Parse(options.GetValueOrDefault("chunk-overlap") ?? "200"),
    Strategy = Enum.Parse<RetrievalStrategy>(options.GetValueOrDefault("strategy") ?? "Dense", ignoreCase: true),
    CandidateK = int.Parse(options.GetValueOrDefault("candidates") ?? "50"),
    RrfK = int.Parse(options.GetValueOrDefault("rrf-k") ?? "60")
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    var dataset = await DatasetLoader.LoadAsync(datasetPath, cts.Token);
    if ((options.GetValueOrDefault("mode") ?? "run").Equals("bench-reranker", StringComparison.OrdinalIgnoreCase))
    {
        var benchmark = await Rag.Evaluation.Cli.RerankerBenchmark.RunAsync(
            options.GetValueOrDefault("model-dir") ?? Path.Combine("models", "ms-marco-MiniLM-L6-v2"), dataset, cts.Token);
        Directory.CreateDirectory(outDirectory);
        await File.WriteAllTextAsync(Path.Combine(outDirectory, $"{label}.md"), benchmark, cts.Token);
        Console.WriteLine(benchmark);
        return 0;
    }

    if ((options.GetValueOrDefault("mode") ?? "run").Equals("bench-memory", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine(await Rag.Evaluation.Cli.RerankerBenchmark.MemoryAsync(
            options.GetValueOrDefault("model-dir") ?? Path.Combine("models", "ms-marco-MiniLM-L6-v2"),
            options.GetValueOrDefault("model-file") ?? "model.onnx", dataset, cts.Token));
        return 0;
    }

    if ((options.GetValueOrDefault("mode") ?? "run").Equals("bench-memory", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine(await Rag.Evaluation.Cli.RerankerBenchmark.MemoryAsync(
            options.GetValueOrDefault("model-dir") ?? Path.Combine("models", "ms-marco-MiniLM-L6-v2"),
            options.GetValueOrDefault("model-file") ?? "model.onnx", dataset, cts.Token));
        return 0;
    }

    var embedder = BuildEmbedder(embedderName, options, configuration);
    var harness = await DenseBaselineHarness.BuildAsync(dataset, configuration, cts.Token, embedder);

    if ((options.GetValueOrDefault("mode") ?? "run").Equals("tune", StringComparison.OrdinalIgnoreCase))
    {
        var tuning = await HybridTuningRun.RunAsync(dataset, harness, cts.Token);
        Directory.CreateDirectory(outDirectory);
        await File.WriteAllTextAsync(Path.Combine(outDirectory, $"{label}.md"), HybridTuningRun.ToMarkdown(tuning, embedder.Model), cts.Token);
        await File.WriteAllTextAsync(Path.Combine(outDirectory, $"{label}.json"),
            System.Text.Json.JsonSerializer.Serialize(tuning, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }), cts.Token);
        Console.WriteLine(HybridTuningRun.ToMarkdown(tuning, embedder.Model));
        return 0;
    }

    var report = await RetrievalEvaluator.EvaluateAsync(
        dataset, harness.Retriever, new EvaluationOptions(), harness.ChunkCatalog, cts.Token);

    var run = new RunRecord
    {
        Label = label,
        Strategy = RetrievalPlan.Resolve(
            new RetrievalOptions { Strategy = configuration.Strategy, CandidateK = configuration.CandidateK, RrfK = configuration.RrfK },
            RetrievalCapabilities.Dense | RetrievalCapabilities.Lexical).StrategyId,
        Embedder = embedder.Model,
        EmbedderKind = embedder.Kind,
        Chunking = configuration,
        DatasetVersion = dataset.DatasetVersion,
        GitCommit = GitCommit(),
        Report = report
    };

    Directory.CreateDirectory(outDirectory);
    await File.WriteAllTextAsync(Path.Combine(outDirectory, $"{label}.json"), RunRecordWriter.ToJson(run), cts.Token);
    await File.WriteAllTextAsync(Path.Combine(outDirectory, $"{label}.md"), RunRecordWriter.ToMarkdown(run), cts.Token);
    Console.WriteLine(RunRecordWriter.ToMarkdown(run));
    return 0;
}
catch (DatasetValidationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (OperationCanceledException)
{
    return 130;
}

static EmbedderSpec BuildEmbedder(string name, Dictionary<string, string> options, HarnessConfiguration configuration)
{
    if (name.Equals("hashing", StringComparison.OrdinalIgnoreCase))
    {
        return EmbedderSpec.Hashing(configuration.VectorSize);
    }

    if (!name.Equals("openai", StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException($"Unknown --embedder '{name}'. Use hashing or openai.");
    }

    // The key is read from the git-ignored .env and is never printed.
    var env = LoadEnv(options.GetValueOrDefault("env") ?? ".env");
    var apiKey = env.GetValueOrDefault("OPENAI__APIKEY");
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        throw new InvalidOperationException("OPENAI__APIKEY is empty in the env file.");
    }

    var model = options.GetValueOrDefault("model") ?? env.GetValueOrDefault("RAG__EMBEDDINGMODEL") ?? "text-embedding-3-small";
    var vectorSize = int.Parse(options.GetValueOrDefault("vector-size") ?? env.GetValueOrDefault("RAG__VECTORSIZE") ?? "1536");
    var inner = new OpenAiEmbeddingProvider(
        Microsoft.Extensions.Options.Options.Create(new OpenAiOptions { ApiKey = apiKey, MaxBatchSize = 128 }),
        Microsoft.Extensions.Options.Options.Create(new RagOptions { EmbeddingProvider = "OpenAI", EmbeddingModel = model, VectorSize = vectorSize }));

    var cached = new FileCachingEmbeddingProvider(inner, model, options.GetValueOrDefault("cache") ?? ".eval-cache");
    return new EmbedderSpec(cached, "OpenAI", model, vectorSize, "real");
}

static Dictionary<string, string> LoadEnv(string path)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var line in File.ReadAllLines(path))
    {
        var trimmed = line.Trim();
        var separator = trimmed.IndexOf('=');
        if (trimmed.StartsWith('#') || separator <= 0)
        {
            continue;
        }

        values[trimmed[..separator].Trim()] = trimmed[(separator + 1)..].Trim().Trim('"');
    }

    return values;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length - 1; i += 2)
    {
        result[args[i].TrimStart('-')] = args[i + 1];
    }

    return result;
}

static string? GitCommit()
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        var commit = process?.StandardOutput.ReadToEnd().Trim();
        process?.WaitForExit();
        return string.IsNullOrEmpty(commit) ? null : commit;
    }
    catch (Exception)
    {
        return null;
    }
}
