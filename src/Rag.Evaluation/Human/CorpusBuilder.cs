using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Rag.Core.Options;
using Rag.Core.Providers.Chunking;

namespace Rag.Evaluation.Human;

public sealed record ChunkingSpec(int ChunkSize, int ChunkOverlap, string TokenizerModel);

public sealed record CorpusFile(string Path, string DocumentId, string Sha256, int Bytes, string Language, string IncludedBecause);

public sealed record ExcludedFile(string Path, string Reason);

public sealed record OwnerSignoff(string By, string SignedAtUtc, string ManifestSha256);

public sealed record SafetyReview(string Scanner, int Findings, OwnerSignoff? OwnerSignoff);

public sealed record CorpusManifest
{
    public string CorpusVersion { get; init; } = string.Empty;

    public string SourceCommit { get; init; } = string.Empty;

    public ChunkingSpec Chunking { get; init; } = new(0, 0, string.Empty);

    public IReadOnlyList<CorpusFile> Files { get; init; } = [];

    public IReadOnlyList<ExcludedFile> Excluded { get; init; } = [];

    public int ChunkCount { get; init; }

    public SafetyReview Safety { get; init; } = new("not run", 0, null);

    /// <summary>
    /// Hash of what the owner is vouching for: the version, chunking and every included file hash. If any of it changes, a previous
    /// sign-off no longer matches and the corpus must be reviewed again.
    /// </summary>
    public string ContentHash()
    {
        var text = string.Join("\n", new[] { CorpusVersion, $"{Chunking.ChunkSize}/{Chunking.ChunkOverlap}/{Chunking.TokenizerModel}" }
            .Concat(Files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => $"{f.Path}:{f.Sha256}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

public sealed class CorpusSafetyException(IReadOnlyList<SafetyFinding> findings)
    : Exception($"{findings.Count} possible sensitive item(s) found; nothing was written. " +
                string.Join("; ", findings.Select(f => $"{f.File}:{f.Line} [{f.Rule}]")))
{
    public IReadOnlyList<SafetyFinding> Findings { get; } = findings;
}

public sealed record CorpusBuildResult(CorpusManifest Manifest, IReadOnlyList<PilotChunk> Chunks, string Topics);

/// <summary>
/// Freezes an explicit allow-list of documents into the pilot corpus: scans them for anything unsafe to send to an external provider
/// <b>before writing a byte</b>, copies them (so later edits to the repository cannot change the corpus), chunks them with the pilot's
/// chunking, and lists neutral topic prompts for the people who will write queries.
/// </summary>
public static class CorpusBuilder
{
    public static async Task<CorpusBuildResult> BuildAsync(
        string repositoryRoot,
        string outputDirectory,
        IReadOnlyList<(string Path, string Reason)> allowList,
        IReadOnlyList<ExcludedFile> excluded,
        ChunkingSpec chunking,
        string corpusVersion,
        string sourceCommit,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? topicPrompts = null)
    {
        var texts = new List<(string Path, string Text, string Reason)>();
        var findings = new List<SafetyFinding>();
        foreach (var (path, reason) in allowList)
        {
            var text = (await File.ReadAllTextAsync(Path.Combine(repositoryRoot, path), ct)).Replace("\r\n", "\n");
            findings.AddRange(SafetyScanner.Scan(path, text));
            texts.Add((path, text, reason));
        }

        if (findings.Count > 0)
        {
            throw new CorpusSafetyException(findings);   // before anything is written
        }

        var corpusDirectory = Path.Combine(outputDirectory, "corpus");
        Directory.CreateDirectory(corpusDirectory);

        var chunker = new DataIngestionTokenChunker(
            Microsoft.Extensions.Options.Options.Create(new ChunkingOptions { ChunkSize = chunking.ChunkSize, ChunkOverlap = chunking.ChunkOverlap }),
            Microsoft.Extensions.Options.Options.Create(new RagOptions { EmbeddingModel = chunking.TokenizerModel }));

        var files = new List<CorpusFile>();
        var chunks = new List<PilotChunk>();
        var topics = new StringBuilder();
        foreach (var (path, text, reason) in texts)
        {
            var documentId = DocumentId(path);
            var language = DetectLanguage(text);
            var title = Title(text) ?? documentId;
            await File.WriteAllTextAsync(Path.Combine(corpusDirectory, documentId + ".md"), text, new UTF8Encoding(false), ct);
            files.Add(new CorpusFile(path, documentId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), Encoding.UTF8.GetByteCount(text), language, reason));

            foreach (var chunk in await chunker.ChunkAsync(documentId, text, null, ct))
            {
                chunks.Add(new PilotChunk { ChunkId = chunk.ChunkId, DocumentId = documentId, Title = title, Language = language, Text = chunk.Text });
            }

            // A neutral, hand-written area when one is given: a document's own title often states its conclusion and would pull query writers toward its wording.
            var topic = topicPrompts is not null && topicPrompts.TryGetValue(path, out var prompt) ? prompt : title;
            topics.AppendLine($"- {topic}  ({(language == "tr" ? "document in Turkish" : "document in English")})");
        }

        var manifest = new CorpusManifest
        {
            CorpusVersion = corpusVersion,
            SourceCommit = sourceCommit,
            Chunking = chunking,
            Files = files,
            Excluded = excluded,
            ChunkCount = chunks.Count,
            Safety = new SafetyReview("SafetyScanner: clean", 0, OwnerSignoff: null)
        };

        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "corpus-manifest.json"), PilotJson.Serialize(manifest), ct);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "chunks.json"), PilotJson.Serialize(chunks), ct);
        return new CorpusBuildResult(manifest, chunks, topics.ToString());
    }

    /// <summary>readme, kb-06b-reranking, adr-0001-hybrid-search-not-default, …</summary>
    public static string DocumentId(string path)
    {
        var trimmed = path.Replace('\\', '/');
        if (trimmed.StartsWith("docs/", StringComparison.Ordinal))
        {
            trimmed = trimmed["docs/".Length..];
        }

        if (trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3];
        }

        return trimmed.Replace('/', '-').ToLowerInvariant();
    }

    public static string? Title(string markdown) =>
        markdown.Split('\n').FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim();

    /// <summary>"tr" when Turkish-specific letters make up at least 0.4 % of the letters, else "en". A heuristic recorded in the manifest, not a guarantee.</summary>
    public static string DetectLanguage(string text)
    {
        var letters = text.Count(char.IsLetter);
        var turkish = text.Count(c => "çğıöşüÇĞİÖŞÜ".Contains(c));
        return letters > 0 && turkish / (double)letters >= 0.004 ? "tr" : "en";
    }

    /// <summary>The owner's sign-off, bound to the manifest content hash.</summary>
    public static CorpusManifest Sign(CorpusManifest manifest, string by, DateTimeOffset at) =>
        manifest with { Safety = manifest.Safety with { OwnerSignoff = new OwnerSignoff(by, at.UtcDateTime.ToString("o"), manifest.ContentHash()) } };

    public static bool IsSignedOff(CorpusManifest manifest) =>
        manifest.Safety.OwnerSignoff is { } s && string.Equals(s.ManifestSha256, manifest.ContentHash(), StringComparison.Ordinal);
}
