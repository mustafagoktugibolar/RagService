using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rag.Evaluation.Human;

/// <summary>Where lines of an advisory category occur in a file. Carries line numbers only, never the matched text.</summary>
public sealed record AdvisoryFinding(string File, string Category, int Count, IReadOnlyList<int> Lines);

/// <summary>
/// Broader, <b>advisory</b> heuristics for the human reviewer: things that are not secrets (the blocking scanner covers those) but that a
/// person deciding "is this safe to send to a third party?" may want to look at — default-credential words, personal or company terms, hosts,
/// URLs, environment-variable names, money figures. Findings never block anything and never print what matched.
/// </summary>
public static class AdvisoryReview
{
    private static readonly (string Category, Regex Pattern)[] BaseRules =
    [
        ("default-credential words", new Regex(@"minioadmin|ragaccess|ragsecret|\bguest\b|Username=|password\s*[:=]", RegexOptions.IgnoreCase)),
        ("company or domain terms", new Regex(@"aircraft|aviation|airline|definition\s?seeder|plan\.md|customer|proprietary|confidential", RegexOptions.IgnoreCase)),
        ("hosts and ports", new Regex(@"\blocalhost:\d+|\b[a-z][a-z0-9-]*:\d{4,5}\b", RegexOptions.IgnoreCase)),
        ("URLs", new Regex(@"https?://", RegexOptions.IgnoreCase)),
        ("environment-variable names", new Regex(@"\b[A-Z][A-Z0-9]*__[A-Z0-9_]+\b")),
        ("money figures", new Regex(@"\$\d"))
    ];

    public static IReadOnlyList<AdvisoryFinding> Scan(IReadOnlyDictionary<string, string> documents, IReadOnlyList<string> personalTerms)
    {
        var rules = BaseRules.ToList();
        var terms = personalTerms.Where(t => t.Length >= 3).Select(Regex.Escape).ToList();
        if (terms.Count > 0)
        {
            rules.Add(("personal names or accounts", new Regex(string.Join('|', terms), RegexOptions.IgnoreCase)));
        }

        var findings = new List<AdvisoryFinding>();
        foreach (var (file, text) in documents.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            var lines = text.Split('\n');
            foreach (var (category, pattern) in rules)
            {
                var hits = Enumerable.Range(0, lines.Length).Where(i => pattern.IsMatch(lines[i])).Select(i => i + 1).ToList();
                if (hits.Count > 0)
                {
                    findings.Add(new AdvisoryFinding(file, category, hits.Count, hits.Take(8).ToList()));
                }
            }
        }

        return findings;
    }
}

public sealed record DocumentProvenance(
    string DocumentId,
    string Path,
    int Commits,
    IReadOnlyList<string> Authors,
    bool AiAssisted,
    bool? OnOriginMain,
    bool? IdenticalToOriginBranch);

public sealed record EmbeddingCacheCheck(int TotalChunks, int CachedChunks, int CachedEntriesInCache, long ApproximateTokens);

/// <summary>Builds the report a human reads before approving the corpus for sending. Facts come from the manifest, git and the caches; nothing here is a judgement.</summary>
public static class CorpusApproval
{
    public static string Build(
        CorpusManifest manifest,
        IReadOnlyList<PilotChunk> chunks,
        IReadOnlyList<DocumentProvenance> provenance,
        IReadOnlyList<AdvisoryFinding> advisory,
        EmbeddingCacheCheck cache,
        string? reviewerNotes,
        string reviewerNotesHashLine)
    {
        var hash = manifest.ContentHash();
        var b = new StringBuilder();
        b.AppendLine("# Corpus approval report — `" + manifest.CorpusVersion + "`");
        b.AppendLine();
        b.AppendLine("Generated from the manifest, the frozen copies, git and the local caches by `--mode pilot-approval-report`. **Nothing was sent anywhere to produce it.**");
        b.AppendLine();
        b.AppendLine("## 1. Status");
        b.AppendLine();
        b.AppendLine("| | |");
        b.AppendLine("|---|---|");
        b.AppendLine($"| Corpus version | `{manifest.CorpusVersion}` |");
        b.AppendLine($"| Source commit | `{manifest.SourceCommit[..Math.Min(10, manifest.SourceCommit.Length)]}` |");
        b.AppendLine($"| Chunking | {manifest.Chunking.ChunkSize} tokens, {manifest.Chunking.ChunkOverlap} overlap (tokenizer `{manifest.Chunking.TokenizerModel}`) |");
        b.AppendLine($"| Documents / chunks | {manifest.Files.Count} / {manifest.ChunkCount} |");
        b.AppendLine($"| Manifest content hash (what a sign-off is bound to) | `{hash[..16]}…` |");
        b.AppendLine($"| Blocking scanner | {manifest.Safety.Scanner}; findings: {manifest.Safety.Findings} |");
        b.AppendLine($"| Owner sign-off | **{(CorpusBuilder.IsSignedOff(manifest) ? "signed off" : "NOT signed off")}** |");
        b.AppendLine();
        b.AppendLine("## 2. The documents");
        b.AppendLine();
        b.AppendLine("| # | document | source file | language | bytes | chunks | git commits | written with AI assistance | on `origin/main` | identical to the pushed branch copy |");
        b.AppendLine("|---:|---|---|---|---:|---:|---:|---|---|---|");
        var perDocument = chunks.GroupBy(c => c.DocumentId).ToDictionary(g => g.Key, g => g.Count());
        var index = 1;
        foreach (var file in manifest.Files)
        {
            var info = provenance.FirstOrDefault(p => p.DocumentId == file.DocumentId);
            b.AppendLine($"| {index++} | `{file.DocumentId}` | `{file.Path}` | {file.Language} | {file.Bytes} | {perDocument.GetValueOrDefault(file.DocumentId)} | {info?.Commits.ToString(CultureInfo.InvariantCulture) ?? "?"} | {Yes(info?.AiAssisted)} | {Yes(info?.OnOriginMain)} | {Yes(info?.IdenticalToOriginBranch)} |");
        }

        b.AppendLine();
        b.AppendLine("*AI assistance* means at least one commit of the file carries a `Co-Authored-By: Claude` trailer. *On origin/main* and *identical to the pushed branch copy* use the last fetched remote refs and say nothing about what is public today if you have not fetched.");
        b.AppendLine();
        b.AppendLine("Excluded on purpose:");
        b.AppendLine();
        b.AppendLine("| excluded | reason |");
        b.AppendLine("|---|---|");
        foreach (var excluded in manifest.Excluded)
        {
            b.AppendLine($"| `{excluded.Path}` | {excluded.Reason} |");
        }

        b.AppendLine();
        b.AppendLine("## 3. Data-sharing review");
        b.AppendLine();
        b.AppendLine("**What would leave this machine, and when.** The pilot itself sends nothing (BM25, a hashing embedder and an optional local model). Passages and queries would be sent to OpenAI only in a *later, separately approved, paid* step (embeddings to add real-dense candidates to the pool; later, a reranker run on the finished labels). This approval is about that later step.");
        b.AppendLine();
        b.AppendLine("**Blocking scanner** (`send-safety-policy.md`): clean — no key, token, e-mail, user path, IP, connection-string password or secret assignment in any of the 12 files.");
        b.AppendLine();
        b.AppendLine("**Advisory heuristics** — things a person may want to look at. They do not block anything; lines are listed, matched text is never printed:");
        b.AppendLine();
        b.AppendLine("| file | category | lines | first line numbers |");
        b.AppendLine("|---|---|---:|---|");
        foreach (var finding in advisory)
        {
            b.AppendLine($"| `{finding.File}` | {finding.Category} | {finding.Count} | {string.Join(", ", finding.Lines)} |");
        }

        b.AppendLine();
        b.AppendLine("## 4. Reviewer notes");
        b.AppendLine();
        if (string.IsNullOrWhiteSpace(reviewerNotes))
        {
            b.AppendLine("*No reviewer notes file was found.*");
        }
        else if (!reviewerNotesHashLine.Contains(hash[..16], StringComparison.OrdinalIgnoreCase))
        {
            b.AppendLine($"> **The notes below were written for a different version of the corpus (`{reviewerNotesHashLine.Trim()}`; current `{hash[..16]}…`). Treat them as possibly stale.**");
            b.AppendLine();
            b.AppendLine(reviewerNotes);
        }
        else
        {
            b.AppendLine(reviewerNotes);
        }

        b.AppendLine();
        b.AppendLine("## 5. Real OpenAI embeddings already in the local cache — can they be reused?");
        b.AppendLine();
        b.AppendLine($"The on-disk cache (`.eval-cache`, keyed by model and text hash) holds {cache.CachedEntriesInCache} vectors. Of this corpus's {cache.TotalChunks} chunks, **{cache.CachedChunks} are cached**; {cache.TotalChunks - cache.CachedChunks} would need a new embedding call (≈ {cache.ApproximateTokens:N0} tokens by the chars/4 heuristic). Queries do not exist yet, so none are cached.");
        b.AppendLine();
        b.AppendLine("## 6. How to approve");
        b.AppendLine();
        b.AppendLine("1. Read sections 2–4 and skim the frozen copies in `corpus/`.");
        b.AppendLine("2. If you accept sending these documents to an external model provider in the later paid step, run:");
        b.AppendLine("   `dotnet run --project src/Rag.Evaluation.Cli -- --mode pilot-signoff --by <your-id> --confirm \"I reviewed the corpus and it is safe to send to external model providers\"`");
        b.AppendLine("3. Until then **no annotation sheet can be produced**: the pooling step refuses an unsigned corpus. Editing any included file or the chunking afterwards invalidates the sign-off.");
        return b.ToString();
    }

    private static string Yes(bool? value) => value switch { true => "yes", false => "no", null => "unknown" };
}
