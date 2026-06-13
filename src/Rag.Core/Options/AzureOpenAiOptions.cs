namespace Rag.Core.Options;

public sealed class AzureOpenAiOptions
{
    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Deployment { get; set; } = string.Empty;

    public int? Dimensions { get; set; }
}
