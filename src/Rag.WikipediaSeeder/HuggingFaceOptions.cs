namespace Rag.WikipediaSeeder;

public sealed class HuggingFaceOptions
{
    public string Dataset { get; set; } = "rag-datasets/rag-mini-wikipedia";
    public string Config { get; set; } = "text-corpus";
    public string Split { get; set; } = "passages";
    public int BatchSize { get; set; } = 100;
}
