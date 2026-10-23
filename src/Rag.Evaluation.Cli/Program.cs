using System.Diagnostics;
using Rag.Evaluation.Dataset;
using Rag.Evaluation.Evaluation;
using Rag.Evaluation.Harness;

// Usage: dotnet run --project src/Rag.Evaluation.Cli -- [--dataset <path>] [--out <dir>] [--label <name>]
//                                                        [--chunk-size <n>] [--chunk-overlap <n>]
var options = ParseArgs(args);
var datasetPath = options.GetValueOrDefault("dataset") ?? Path.Combine("datasets", "golden-v1", "dataset.json");
var outDirectory = options.GetValueOrDefault("out") ?? Path.Combine("docs", "eval");
var label = options.GetValueOrDefault("label") ?? "baseline-dense";

var configuration = new HarnessConfiguration
{
    ChunkSize = int.Parse(options.GetValueOrDefault("chunk-size") ?? "1000"),
    ChunkOverlap = int.Parse(options.GetValueOrDefault("chunk-overlap") ?? "200")
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    var dataset = await DatasetLoader.LoadAsync(datasetPath, cts.Token);
    var harness = await DenseBaselineHarness.BuildAsync(dataset, configuration, cts.Token);
    var report = await RetrievalEvaluator.EvaluateAsync(
        dataset, harness.Retriever, new EvaluationOptions(), harness.ChunkCatalog, cts.Token);

    var run = new RunRecord
    {
        Label = label,
        Strategy = "dense",
        Embedder = HashingEmbeddingProvider.ModelName,
        EmbedderKind = "synthetic",
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
