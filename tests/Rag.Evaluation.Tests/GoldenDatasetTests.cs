using Rag.Evaluation.Dataset;

namespace Rag.Evaluation.Tests;

public sealed class GoldenDatasetTests
{
    private static string DatasetPath => Path.Combine(AppContext.BaseDirectory, "datasets", "golden-v1", "dataset.json");

    [Fact]
    public async Task GoldenV1_PassesValidation_AndCoversEveryQueryCategory()
    {
        var dataset = await DatasetLoader.LoadAsync(DatasetPath, CancellationToken.None);

        Assert.Equal("golden-v1.0.0", dataset.DatasetVersion);
        Assert.True(dataset.Queries.Count >= 40, $"expected >= 40 queries, found {dataset.Queries.Count}");

        var categories = dataset.Queries.Select(q => q.Category).ToHashSet();
        Assert.Superset(new HashSet<string?> { "keyword", "paraphrase", "multi-relevant", "conceptual", "no-answer" }, categories);
        Assert.Contains(dataset.Queries, q => q.RelevantDocuments.Count == 0);
        Assert.Contains(dataset.Queries, q => q.RelevantDocuments.Select(r => r.Grade).Distinct().Count() > 1);
    }
}
