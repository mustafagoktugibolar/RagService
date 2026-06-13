namespace Rag.Core.Options;

public sealed class OpenAiOptions
{
    public string ApiKey { get; set; } = string.Empty;

    public string? BaseUrl { get; set; }

    public string? Organization { get; set; }

    public string? Project { get; set; }

    public int? Dimensions { get; set; }

    public int MaxBatchSize { get; set; } = 2048;
}
