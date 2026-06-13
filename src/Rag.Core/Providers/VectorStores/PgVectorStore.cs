using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using Pgvector.Npgsql;
using Rag.Core.Abstractions;
using Rag.Core.Models;
using Rag.Core.Options;

namespace Rag.Core.Providers.VectorStores;

public sealed class PgVectorStore : IVectorStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;
    private readonly PgVectorOptions _options;

    public PgVectorStore(IOptions<PgVectorOptions> options)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new InvalidOperationException("PgVector:ConnectionString is required when Rag:VectorStoreProvider is PgVector.");
        }

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(_options.ConnectionString);
        dataSourceBuilder.UseVector();
        _dataSource = dataSourceBuilder.Build();
    }

    public async Task EnsureCollectionAsync(string collection, int vectorSize, CancellationToken ct)
    {
        await EnsureTablesAsync(ct);
    }

    public async Task UpsertAsync(
        string collection,
        IReadOnlyList<VectorDocument> documents,
        CancellationToken ct)
    {
        if (documents.Count == 0)
        {
            return;
        }

        await EnsureTablesAsync(ct);
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var document in documents)
        {
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {VectorTable} (collection, document_id, chunk_id, text, metadata, embedding)
                VALUES (@collection, @documentId, @chunkId, @text, CAST(@metadata AS jsonb), @embedding)
                ON CONFLICT (collection, chunk_id)
                DO UPDATE SET
                    document_id = EXCLUDED.document_id,
                    text = EXCLUDED.text,
                    metadata = EXCLUDED.metadata,
                    embedding = EXCLUDED.embedding
                """, connection, transaction);

            command.Parameters.AddWithValue("collection", collection);
            command.Parameters.AddWithValue("documentId", document.DocumentId);
            command.Parameters.AddWithValue("chunkId", document.ChunkId);
            command.Parameters.AddWithValue("text", document.Text);
            command.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(document.Metadata ?? new Dictionary<string, string>(), JsonOptions));
            command.Parameters.AddWithValue("embedding", new Vector(document.Embedding));

            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collection,
        float[] embedding,
        int topK,
        Dictionary<string, string>? filters,
        CancellationToken ct)
    {
        await EnsureTablesAsync(ct);
        await using var connection = await OpenConnectionAsync(ct);

        var filterClauses = new System.Text.StringBuilder();
        var filterParams = new List<(string keyParam, string keyValue, string valParam, string valValue)>();
        if (filters is not null)
        {
            var i = 0;
            foreach (var (key, value) in filters)
            {
                var kp = $"fk{i}";
                var vp = $"fv{i}";
                filterClauses.Append($"\n  AND jsonb_extract_path_text(metadata, @{kp}) = @{vp}");
                filterParams.Add((kp, key, vp, value));
                i++;
            }
        }

        await using var command = new NpgsqlCommand($"""
            SELECT document_id,
                   chunk_id,
                   text,
                   metadata::text,
                   1 - (embedding <=> @embedding) AS score
            FROM {VectorTable}
            WHERE collection = @collection{filterClauses}
            ORDER BY embedding <=> @embedding
            LIMIT @topK
            """, connection);

        command.Parameters.AddWithValue("collection", collection);
        command.Parameters.AddWithValue("embedding", new Vector(embedding));
        command.Parameters.AddWithValue("topK", topK);
        foreach (var (kp, kv, vp, vv) in filterParams)
        {
            command.Parameters.AddWithValue(kp, kv);
            command.Parameters.AddWithValue(vp, vv);
        }

        var results = new List<VectorSearchResult>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var metadataJson = reader.GetString(3);
            results.Add(new VectorSearchResult
            {
                DocumentId = reader.GetString(0),
                ChunkId = reader.GetString(1),
                Text = reader.GetString(2),
                Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, JsonOptions),
                Score = reader.GetDouble(4)
            });
        }

        return results;
    }

    public async Task DeleteByDocumentIdAsync(string collection, string documentId, CancellationToken ct)
    {
        await EnsureTablesAsync(ct);
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand($"""
            DELETE FROM {VectorTable}
            WHERE collection = @collection
              AND document_id = @documentId
            """, connection);

        command.Parameters.AddWithValue("collection", collection);
        command.Parameters.AddWithValue("documentId", documentId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    public async Task<CollectionMetadata?> GetCollectionMetadataAsync(string collection, CancellationToken ct)
    {
        await EnsureTablesAsync(ct);
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand($"""
            SELECT collection, embedding_provider, embedding_model, vector_size
            FROM {MetadataTable}
            WHERE collection = @collection
            """, connection);

        command.Parameters.AddWithValue("collection", collection);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new CollectionMetadata
        {
            Collection = reader.GetString(0),
            EmbeddingProvider = reader.GetString(1),
            EmbeddingModel = reader.GetString(2),
            VectorSize = reader.GetInt32(3)
        };
    }

    public async Task SetCollectionMetadataAsync(CollectionMetadata metadata, CancellationToken ct)
    {
        await EnsureTablesAsync(ct);
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {MetadataTable} (collection, embedding_provider, embedding_model, vector_size)
            VALUES (@collection, @embeddingProvider, @embeddingModel, @vectorSize)
            ON CONFLICT (collection)
            DO UPDATE SET
                embedding_provider = EXCLUDED.embedding_provider,
                embedding_model = EXCLUDED.embedding_model,
                vector_size = EXCLUDED.vector_size
            """, connection);

        command.Parameters.AddWithValue("collection", metadata.Collection);
        command.Parameters.AddWithValue("embeddingProvider", metadata.EmbeddingProvider);
        command.Parameters.AddWithValue("embeddingModel", metadata.EmbeddingModel);
        command.Parameters.AddWithValue("vectorSize", metadata.VectorSize);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureTablesAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand($"""
            CREATE EXTENSION IF NOT EXISTS vector;

            CREATE TABLE IF NOT EXISTS {VectorTable} (
                collection text NOT NULL,
                document_id text NOT NULL,
                chunk_id text NOT NULL,
                text text NOT NULL,
                metadata jsonb NOT NULL DEFAULT jsonb_build_object(),
                embedding vector NOT NULL,
                PRIMARY KEY (collection, chunk_id)
            );

            CREATE INDEX IF NOT EXISTS {IndexName("ix_rag_vectors_collection_document")}
                ON {VectorTable} (collection, document_id);

            CREATE TABLE IF NOT EXISTS {MetadataTable} (
                collection text PRIMARY KEY,
                embedding_provider text NOT NULL,
                embedding_model text NOT NULL,
                vector_size integer NOT NULL
            );
            """, connection);

        await command.ExecuteNonQueryAsync(ct);
        await connection.ReloadTypesAsync(ct);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        return await _dataSource.OpenConnectionAsync(ct);
    }

    private string VectorTable => QuoteQualifiedIdentifier(_options.TableName);

    private string MetadataTable => QuoteQualifiedIdentifier(_options.MetadataTableName);

    private string IndexName(string fallback)
    {
        var tableName = _options.TableName.Split('.').LastOrDefault() ?? fallback;
        return QuoteIdentifier($"{fallback}_{tableName}".Trim('_'));
    }

    private static string QuoteQualifiedIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("PostgreSQL table names cannot be empty.");
        }

        return string.Join('.', value.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(QuoteIdentifier));
    }

    private static string QuoteIdentifier(string value)
    {
        if (value.Length == 0 || value.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
        {
            throw new InvalidOperationException($"Invalid PostgreSQL identifier '{value}'. Use letters, digits, underscores, and optional schema qualification only.");
        }

        return $"\"{value}\"";
    }
}
