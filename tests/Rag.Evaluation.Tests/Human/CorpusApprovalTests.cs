using Rag.Evaluation.Human;

namespace Rag.Evaluation.Tests.Human;

public sealed class CorpusApprovalTests
{
    private static CorpusManifest Manifest(bool signed = false)
    {
        var manifest = new CorpusManifest
        {
            CorpusVersion = "v-test", SourceCommit = "0123456789abcdef", Chunking = new ChunkingSpec(250, 40, "m"),
            Files = [new CorpusFile("README.md", "readme", "H1", 100, "en", "overview"), new CorpusFile("docs/kb/x.md", "kb-x", "H2", 200, "tr", "kb")],
            Excluded = [new ExcludedFile("plan.md", "company-specific")], ChunkCount = 3, Safety = new SafetyReview("clean", 0, null)
        };
        return signed ? CorpusBuilder.Sign(manifest, "owner", DateTimeOffset.UtcNow) : manifest;
    }

    private static readonly PilotChunk[] Chunks =
    [
        new() { ChunkId = "readme:000000", DocumentId = "readme" }, new() { ChunkId = "readme:000001", DocumentId = "readme" }, new() { ChunkId = "kb-x:000000", DocumentId = "kb-x" }
    ];

    private static readonly DocumentProvenance[] Provenance =
    [
        new("readme", "README.md", 5, ["owner"], true, true, true), new("kb-x", "docs/kb/x.md", 2, ["owner"], true, false, false)
    ];

    private static string Build(CorpusManifest? manifest = null, string? notes = "A note.", string notesHash = "manifest-hash: ", EmbeddingCacheCheck? cache = null) =>
        CorpusApproval.Build(manifest ?? Manifest(), Chunks, Provenance, [new AdvisoryFinding("readme", "URLs", 3, [4, 9, 20])],
            cache ?? new EmbeddingCacheCheck(3, 0, 79, 1200), notes, notesHash);

    [Fact]
    public void EveryDocumentIsListedWithItsSourceLanguageChunksAndProvenance()
    {
        var report = Build();

        Assert.Contains("| 1 | `readme` | `README.md` | en | 100 | 2 | 5 | yes | yes | yes |", report);
        Assert.Contains("| 2 | `kb-x` | `docs/kb/x.md` | tr | 200 | 1 | 2 | yes | no | no |", report);
    }

    [Fact]
    public void ExclusionsAndTheirReasonsAreShown() => Assert.Contains("| `plan.md` | company-specific |", Build());

    [Fact]
    public void TheSignOffStatusIsExplicit_AndWithoutItNoSheetCanBeProduced()
    {
        Assert.Contains("**NOT signed off**", Build());
        Assert.Contains("no annotation sheet can be produced", Build());
        Assert.Contains("**signed off**", Build(Manifest(signed: true)));
    }

    [Fact]
    public void AdvisoryFindingsListLineNumbersAndCategories_NeverMatchedText()
    {
        var report = Build();

        Assert.Contains("| `readme` | URLs | 3 | 4, 9, 20 |", report);
        Assert.Contains("matched text is never printed", report);
    }

    [Fact]
    public void ReviewerNotes_WrittenForAnotherCorpusVersion_AreMarkedPossiblyStale()
    {
        var current = Build(notes: "Some note", notesHash: "manifest-hash: 0000000000000000");

        Assert.Contains("possibly stale", current);
        var matching = CorpusApproval.Build(Manifest(), Chunks, Provenance, [], new EmbeddingCacheCheck(3, 0, 0, 0), "Some note", $"manifest-hash: {Manifest().ContentHash()[..16]}");
        Assert.DoesNotContain("possibly stale", matching);
    }

    [Fact]
    public void TheCacheSectionStatesHowManyChunksNeedANewEmbedding()
    {
        var report = Build(cache: new EmbeddingCacheCheck(213, 0, 79, 36259));

        Assert.Contains("**0 are cached**", report);
        Assert.Contains("213 would need a new embedding call", report);
        Assert.Contains("36,259", report);
    }

    [Fact]
    public void ThePilotItselfIsStatedToSendNothing() => Assert.Contains("The pilot itself sends nothing", Build());

    [Fact]
    public void AdvisoryScan_ReportsCountsAndLines_ButNotTheMatchedWords()
    {
        var docs = new Dictionary<string, string>
        {
            ["a.md"] = "intro\nthe default user is minioadmin here\nsee https://example.org/x\nand again https://example.org/y\nmaintainer Jane Doe wrote this",
            ["b.md"] = "nothing special at all"
        };

        var findings = AdvisoryReview.Scan(docs, ["jane doe"]);

        var credentials = Assert.Single(findings, f => f.Category == "default-credential words");
        Assert.Equal(("a.md", 1, 2), (credentials.File, credentials.Count, credentials.Lines[0]));
        Assert.Equal(2, Assert.Single(findings, f => f.Category == "URLs").Count);
        Assert.Contains(findings, f => f.Category == "personal names or accounts" && f.Lines.Contains(5));
        Assert.DoesNotContain(findings, f => f.File == "b.md");
        Assert.DoesNotContain("minioadmin", string.Join("|", findings.Select(f => f.ToString())));
    }
}
