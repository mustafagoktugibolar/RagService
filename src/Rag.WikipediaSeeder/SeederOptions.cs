namespace Rag.WikipediaSeeder;

public sealed class SeederOptions
{
    public string Collection { get; set; } = "wikipedia";
    public int MaxDocuments { get; set; } = 0;
    public string IndexQueueName { get; set; } = "rag.index";
}
