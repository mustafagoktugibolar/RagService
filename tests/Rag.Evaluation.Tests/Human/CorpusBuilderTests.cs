using Rag.Evaluation.Human;

namespace Rag.Evaluation.Tests.Human;

public sealed class CorpusBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"corpus-{Guid.NewGuid():N}");
    private readonly string _output;

    public CorpusBuilderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "kb"));
        _output = Path.Combine(_root, "out");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string path, string text) => File.WriteAllText(Path.Combine(_root, path), text);

    private static readonly ChunkingSpec Spec = new(60, 10, "text-embedding-3-small");

    private Task<CorpusBuildResult> Build(params string[] paths) => CorpusBuilder.BuildAsync(
        _root, _output, paths.Select(p => (p, "technical documentation")).ToList(), [new ExcludedFile("plan.md", "company-specific")],
        Spec, "test-corpus-v1", "abc1234", CancellationToken.None);

    private string LongText(string title, string word) =>
        $"# {title}\n\n" + string.Join("\n\n", Enumerable.Range(0, 12).Select(i => $"Paragraph {i} about {word}. " + string.Concat(Enumerable.Repeat($"{word} detail sentence number {i}. ", 6))));

    [Fact]
    public async Task Documents_AreFrozenIntoACopy_ChunkedWithStableIds_AndTitled()
    {
        Write("README.md", LongText("Project Readme", "alpha"));
        Write("docs/kb/01-arch.md", LongText("Architecture", "beta"));

        var result = await Build("README.md", "docs/kb/01-arch.md");

        Assert.Equal(["readme", "kb-01-arch"], result.Manifest.Files.Select(f => f.DocumentId));
        Assert.True(File.Exists(Path.Combine(_output, "corpus", "readme.md")));
        Assert.True(result.Chunks.Count > 4, "documents of this size must split into several chunks at 60 tokens");
        Assert.All(result.Chunks, c => Assert.StartsWith($"{c.DocumentId}:", c.ChunkId));
        Assert.Equal("Project Readme", result.Chunks.First(c => c.DocumentId == "readme").Title);
        Assert.Equal(result.Manifest.ChunkCount, result.Chunks.Count);
        Assert.True(File.Exists(Path.Combine(_output, "corpus-manifest.json")) && File.Exists(Path.Combine(_output, "chunks.json")));
    }

    [Fact]
    public async Task ChunkIdsAndHashes_AreDeterministic()
    {
        Write("README.md", LongText("Project Readme", "alpha"));

        var first = await Build("README.md");
        var second = await Build("README.md");

        Assert.Equal(first.Chunks.Select(c => c.ChunkId), second.Chunks.Select(c => c.ChunkId));
        Assert.Equal(first.Manifest.Files[0].Sha256, second.Manifest.Files[0].Sha256);
        Assert.Equal(first.Manifest.ContentHash(), second.Manifest.ContentHash());
    }

    [Fact]
    public async Task AnUnsafeDocument_BlocksTheWholeBuild_BeforeAnythingIsWritten()
    {
        Write("README.md", LongText("Project Readme", "alpha"));
        Write("docs/kb/02-leak.md", "# Leaky\n\nReach us at someone@example.com or see /Users/someone/project.");

        var ex = await Assert.ThrowsAsync<CorpusSafetyException>(() => Build("README.md", "docs/kb/02-leak.md"));

        Assert.Equal(["email", "local-user-path"], ex.Findings.Select(f => f.Rule).Order());
        Assert.False(Directory.Exists(_output), "no output may exist when the scan fails");
        Assert.DoesNotContain("someone@example.com", ex.Message);
    }

    [Fact]
    public async Task ExclusionsAreRecordedWithTheirReason()
    {
        Write("README.md", LongText("Project Readme", "alpha"));

        var result = await Build("README.md");

        Assert.Equal(("plan.md", "company-specific"), (result.Manifest.Excluded[0].Path, result.Manifest.Excluded[0].Reason));
    }

    [Theory]
    [InlineData("README.md", "readme")]
    [InlineData("docs/kb/06b-reranking.md", "kb-06b-reranking")]
    [InlineData("docs/adr/0001-Hybrid-Search.md", "adr-0001-hybrid-search")]
    [InlineData(@"docs\research\x.md", "research-x")]
    public void DocumentIds_AreDerivedFromThePath(string path, string expected) => Assert.Equal(expected, CorpusBuilder.DocumentId(path));

    [Theory]
    [InlineData("Bu bölüm Türkçe yazılmıştır ve çok sayıda özel harf içerir: ğüşiöç.", "tr")]
    [InlineData("This text is plain English with no special letters at all.", "en")]
    public void LanguageIsDetectedFromTurkishLetters(string text, string expected) => Assert.Equal(expected, CorpusBuilder.DetectLanguage(text));

    [Fact]
    public async Task SignOff_IsBoundToTheContent_SoAnyChangeInvalidatesIt()
    {
        Write("README.md", LongText("Project Readme", "alpha"));
        var manifest = (await Build("README.md")).Manifest;
        Assert.False(CorpusBuilder.IsSignedOff(manifest));

        var signed = CorpusBuilder.Sign(manifest, "owner", DateTimeOffset.UtcNow);
        Assert.True(CorpusBuilder.IsSignedOff(signed));

        var edited = signed with { Files = [signed.Files[0] with { Sha256 = "DIFFERENT" }] };
        Assert.False(CorpusBuilder.IsSignedOff(edited));                                               // a changed file
        Assert.False(CorpusBuilder.IsSignedOff(signed with { Chunking = Spec with { ChunkSize = 250 } }));   // a changed chunking
    }

    [Fact]
    public async Task TopicPrompts_ListTitlesOnly_NeverPassageText()
    {
        Write("README.md", LongText("Project Readme", "alpha"));

        var result = await Build("README.md");

        Assert.Contains("Project Readme", result.Topics);
        Assert.DoesNotContain("Paragraph", result.Topics);
        Assert.DoesNotContain("detail sentence", result.Topics);
    }

    [Fact]
    public async Task WhenNeutralAreasAreSupplied_TheTopicPromptsUseThemInsteadOfDocumentTitles()
    {
        Write("README.md", LongText("A Title That States The Conclusion Hybrid Is Off", "alpha"));

        var result = await CorpusBuilder.BuildAsync(
            _root, _output, [("README.md", "doc")], [], Spec, "v", "abc", CancellationToken.None,
            new Dictionary<string, string> { ["README.md"] = "A recorded decision about the default search strategy" });

        Assert.Contains("A recorded decision about the default search strategy", result.Topics);
        Assert.DoesNotContain("States The Conclusion", result.Topics);
        Assert.Contains("document in English", result.Topics);
    }
}
