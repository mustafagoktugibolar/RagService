namespace Rag.Core.Options;

public sealed class PgVectorOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    public string TableName { get; set; } = "rag_vectors";

    public string MetadataTableName { get; set; } = "rag_collection_metadata";
}
