namespace Rag.Core.Options;

public sealed class MinioOptions
{
    public string Endpoint { get; set; } = "localhost:9000";
    public string AccessKey { get; set; } = "minioadmin";
    public string SecretKey { get; set; } = "minioadmin";
    public bool WithSsl { get; set; } = false;
    public string Bucket { get; set; } = "rag-documents";
}
