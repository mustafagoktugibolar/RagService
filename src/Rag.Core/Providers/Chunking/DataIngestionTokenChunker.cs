using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using Rag.Core.Abstractions;
using Rag.Core.Models;
using Rag.Core.Options;

namespace Rag.Core.Providers.Chunking;

public sealed class DataIngestionTokenChunker(
    IOptions<ChunkingOptions> options,
    IOptions<RagOptions> ragOptions) : IChunker
{
    public async Task<IReadOnlyList<DocumentChunk>> ChunkAsync(
        string documentId,
        string text,
        Dictionary<string, string>? metadata,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var chunkingOptions = options.Value;
        var maxTokens = chunkingOptions.ChunkSize;
        var overlapTokens = chunkingOptions.ChunkOverlap;

        if (maxTokens <= 0)
        {
            throw new InvalidOperationException("Chunking:ChunkSize must be greater than zero.");
        }

        if (overlapTokens < 0 || overlapTokens >= maxTokens)
        {
            throw new InvalidOperationException("Chunking:ChunkOverlap must be greater than or equal to zero and less than ChunkSize.");
        }

        var ingestionOptions = new IngestionChunkerOptions(CreateTokenizer(ragOptions.Value.EmbeddingModel))
        {
            MaxTokensPerChunk = maxTokens,
            OverlapTokens = overlapTokens
        };

        var chunker = new DocumentTokenChunker(ingestionOptions);
        var document = CreateDocument(documentId, text);
        var chunks = new List<DocumentChunk>();
        var index = 0;

        await foreach (var chunk in chunker.ProcessAsync(document, ct))
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(chunk.Content))
            {
                continue;
            }

            chunks.Add(new DocumentChunk
            {
                DocumentId = documentId,
                ChunkId = $"{documentId}:{index:D6}",
                Text = chunk.Content.Trim(),
                Metadata = metadata is null ? null : new Dictionary<string, string>(metadata)
            });
            index++;
        }

        return chunks;
    }

    private static IngestionDocument CreateDocument(string documentId, string text)
    {
        var document = new IngestionDocument(documentId);
        var section = new IngestionDocumentSection();

        foreach (var paragraph in SplitParagraphs(text))
        {
            section.Elements.Add(new IngestionDocumentParagraph(paragraph));
        }

        if (section.Elements.Count == 0)
        {
            section.Elements.Add(new IngestionDocumentParagraph(text.Trim()));
        }

        document.Sections.Add(section);
        return document;
    }

    private static TiktokenTokenizer CreateTokenizer(string model)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            try
            {
                return TiktokenTokenizer.CreateForModel(model);
            }
            catch (ArgumentException)
            {
            }
        }

        return TiktokenTokenizer.CreateForModel("gpt-4o");
    }

    private static IEnumerable<string> SplitParagraphs(string text)
    {
        using var reader = new StringReader(text);
        var builder = new List<string>();
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (builder.Count > 0)
                {
                    yield return string.Join(Environment.NewLine, builder).Trim();
                    builder.Clear();
                }

                continue;
            }

            builder.Add(line.TrimEnd());
        }

        if (builder.Count > 0)
        {
            yield return string.Join(Environment.NewLine, builder).Trim();
        }
    }
}
