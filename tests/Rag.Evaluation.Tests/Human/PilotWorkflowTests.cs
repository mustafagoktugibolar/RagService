using System.Text.Json;
using Rag.Evaluation.Human;

namespace Rag.Evaluation.Tests.Human;

/// <summary>
/// The whole pilot pipeline over a synthetic mini-repository in a temp directory. The "annotators" here are code that fills sheets by a
/// fixed rule: test fixtures only. No real pilot data is touched and no model is involved.
/// </summary>
public sealed class PilotWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pilot-{Guid.NewGuid():N}");
    private readonly string _dataset;
    private readonly PilotWorkflow _workflow;

    private static readonly string[] Words = ["zebra", "quartz", "falcon", "walnut", "harbor", "violet"];

    public PilotWorkflowTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        _dataset = Path.Combine(_root, "dataset");
        _workflow = new PilotWorkflow(_dataset);
        foreach (var word in Words)
        {
            File.WriteAllText(Path.Combine(_root, "docs", $"{word}.md"),
                $"# Notes about {word}\n\n" + string.Join("\n\n", Enumerable.Range(0, 12).Select(i => $"Section {i} on the {word}. " + string.Concat(Enumerable.Repeat($"The {word} appears in sentence {i} with more {word} detail. ", 7)))));
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly ChunkingSpec Spec = new(60, 10, "text-embedding-3-small");

    private Task<CorpusBuildResult> Freeze() => _workflow.FreezeCorpusAsync(
        _root, Words.Select(w => ($"docs/{w}.md", "synthetic doc")).ToList(), [new ExcludedFile("secret.md", "not for sending")], Spec, "synthetic-v1", "abc1234", CancellationToken.None);

    private void WriteAnnotators()
    {
        Annotator[] people =
        [
            new() { Id = "owner", Kind = "human", Roles = ["query-writer", "annotator", "adjudicator"] },
            new() { Id = "colleague", Kind = "human", Roles = ["query-writer", "annotator"] }
        ];
        File.WriteAllText(Path.Combine(_dataset, "annotators.json"), JsonSerializer.Serialize(people, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private IReadOnlyList<PilotQuery> ImportQueries()
    {
        var plan = new (string Category, int Count)[]
        {
            (PilotCategories.Keyword, 5), (PilotCategories.Paraphrase, 6), (PilotCategories.MultiRelevant, 4),
            (PilotCategories.Conceptual, 4), (PilotCategories.CrossLingual, 3), (PilotCategories.NoAnswer, 2)
        };
        var rows = new List<IReadOnlyList<string>> { new[] { "id", "text", "category", "language", "written_by" } };
        var n = 0;
        foreach (var (category, count) in plan)
        {
            for (var i = 0; i < count; i++, n++)
            {
                var word = category == PilotCategories.NoAnswer ? $"nothing{n}" : Words[n % Words.Length];
                rows.Add([$"pilot-{n:D3}", $"what is said regarding {word} in variant number {n}", category, "en", n % 2 == 0 ? "owner" : "colleague"]);
            }
        }

        return _workflow.ImportQueries(Csv.Write(rows), _workflow.ReadAnnotators());
    }

    private async Task<PoolKey> Prepare(bool sign = true)
    {
        await Freeze();
        WriteAnnotators();
        ImportQueries();
        if (sign)
        {
            _workflow.SignOff("owner", DateTimeOffset.UtcNow);
        }

        return _workflow.BuildPools([], [("golden-x", "completely unrelated golden query")]).Pool;
    }

    /// <summary>Fixture annotator: grade 3 if the passage contains the query's topic word, else 0; B differs from A on every 9th item.</summary>
    private void FillSheets(bool secondDiffers = true)
    {
        foreach (var person in new[] { "owner", "colleague" })
        {
            var rows = Csv.Read(File.ReadAllText(Path.Combine(_dataset, "pools", "sheets", $"annotator-{person}.csv")));
            for (var r = 1; r < rows.Count; r++)
            {
                var query = rows[r][2];
                var word = Words.FirstOrDefault(w => query.Contains(w));
                var grade = word is not null && rows[r][5].Contains(word) ? 3 : 0;
                if (person == "colleague" && secondDiffers && r % 9 == 0)
                {
                    grade = grade == 3 ? 2 : 1;
                }

                rows[r][6] = grade.ToString();
                rows[r][8] = "25";
            }

            Directory.CreateDirectory(Path.Combine(_dataset, "judgments"));
            File.WriteAllText(Path.Combine(_dataset, "judgments", $"annotator-{person}.csv"), Csv.Write(rows.Select(x => (IReadOnlyList<string>)x)));
        }
    }

    [Fact]
    public async Task Freezing_WritesManifestChunksCopiesAndTopicPrompts()
    {
        var result = await Freeze();

        Assert.True(result.Chunks.Count > 40);
        foreach (var file in new[] { "corpus-manifest.json", "chunks.json", "topics.md", Path.Combine("corpus", "zebra.md") })
        {
            Assert.True(File.Exists(Path.Combine(_dataset, file)), file);
        }

        var topics = File.ReadAllText(Path.Combine(_dataset, "topics.md"));
        Assert.Contains("Notes about zebra", topics);   // no neutral area was supplied for the synthetic docs, so the title is the fallback
        Assert.DoesNotContain("appears in sentence", topics);
    }

    [Fact]
    public async Task PoolsCannotBeBuilt_BeforeTheOwnerSignsTheCorpusOff()
    {
        await Freeze();
        WriteAnnotators();
        ImportQueries();

        var ex = Assert.Throws<PilotValidationException>(() => _workflow.BuildPools([], []));

        Assert.Contains(ex.Errors, e => e.Contains("sign-off"));
        Assert.False(Directory.Exists(Path.Combine(_dataset, "pools")), "no sheet may exist before the corpus is signed off");
    }

    [Fact]
    public async Task EditingTheCorpusAfterSignOff_BlocksPooling_UntilItIsReviewedAgain()
    {
        await Prepare();
        var manifest = _workflow.ReadManifest();
        File.WriteAllText(Path.Combine(_dataset, "corpus-manifest.json"),
            JsonSerializer.Serialize(manifest with { Files = [manifest.Files[0] with { Sha256 = "TAMPERED" }, .. manifest.Files.Skip(1)] }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.Throws<PilotValidationException>(() => _workflow.BuildPools([], []));
    }

    [Fact]
    public async Task ASheetIsWrittenPerAnnotator_AndNoSheetOrQueryRevealsAnySystem()
    {
        var pool = await Prepare();

        var sheets = Directory.GetFiles(Path.Combine(_dataset, "pools", "sheets"));

        Assert.Equal(["annotator-colleague.csv", "annotator-owner.csv"], sheets.Select(Path.GetFileName).Order());
        foreach (var sheet in sheets)
        {
            var text = File.ReadAllText(sheet);
            foreach (var system in pool.Systems.Append("random"))
            {
                Assert.DoesNotContain(system, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.True(File.Exists(Path.Combine(_dataset, "pools", "private", "pool-key.json")));
        Assert.DoesNotContain("private", string.Join("|", sheets));
    }

    [Fact]
    public async Task ASimilarOtherQuery_BlocksPooling_ToKeepThePilotSeparateFromTheFinalSet()
    {
        await Freeze();
        WriteAnnotators();
        var queries = ImportQueries();
        _workflow.SignOff("owner", DateTimeOffset.UtcNow);

        var ex = Assert.Throws<PilotValidationException>(() => _workflow.BuildPools([], [("final-007", queries[0].Text)]));

        Assert.Contains(ex.Errors, e => e.Contains("final-007"));
    }

    [Fact]
    public async Task FinalLabels_AreNotWritten_WhileDisagreementsAreUnresolved()
    {
        await Prepare();
        FillSheets();
        var sheet = _workflow.WriteDisagreementSheet();

        Assert.True(File.Exists(sheet));
        var ex = Assert.Throws<PilotValidationException>(() => _workflow.BuildReport([]));

        Assert.Contains(ex.Errors, e => e.Contains("adjudication"));
        Assert.False(File.Exists(Path.Combine(_dataset, "final-judgments.json")));
    }

    [Fact]
    public async Task OnceAHumanAdjudicates_TheFinalLabelsAndTheReportAreProduced()
    {
        await Prepare();
        FillSheets();
        var rows = Csv.Read(File.ReadAllText(_workflow.WriteDisagreementSheet()));
        Assert.True(rows.Count > 1, "the fixture annotators were made to disagree");
        for (var r = 1; r < rows.Count; r++)
        {
            rows[r][9] = rows[r][5];                       // adjudicator sides with annotator A
            rows[r][10] = "A matches the passage";
        }

        Directory.CreateDirectory(Path.Combine(_dataset, "adjudication"));
        File.WriteAllText(Path.Combine(_dataset, "adjudication", "adjudicated.csv"), Csv.Write(rows.Select(x => (IReadOnlyList<string>)x)));

        var (data, inputs, markdown) = _workflow.BuildReport([("golden-x", "completely unrelated golden query")]);

        Assert.True(File.Exists(Path.Combine(_dataset, "final-judgments.json")));
        Assert.Equal(10, data.Criteria.Count);
        Assert.Equal("pass", data.Criteria.Single(c => c.Id == "P1").Status);
        Assert.Equal("pass", data.Criteria.Single(c => c.Id == "P2").Status);
        Assert.Equal("pass", data.Criteria.Single(c => c.Id == "P3").Status);
        Assert.Equal("pass", data.Criteria.Single(c => c.Id == "P4").Status);
        Assert.Equal("pass", data.Criteria.Single(c => c.Id == "P10").Status);
        Assert.Equal(25.0, data.MedianSecondsPerItem);
        Assert.All(inputs.Final, f => Assert.Contains(f.Source, new[] { "agreed", "adjudicated" }));
        Assert.Contains("Human-labeling pilot", markdown);
    }

    [Fact]
    public async Task AnAnnotatorSheetWithAnImpossibleGrade_IsRejectedWithAClearMessage()
    {
        await Prepare();
        FillSheets(secondDiffers: false);
        var path = Path.Combine(_dataset, "judgments", "annotator-owner.csv");
        var rows = Csv.Read(File.ReadAllText(path));
        rows[1][6] = "9";
        File.WriteAllText(path, Csv.Write(rows.Select(x => (IReadOnlyList<string>)x)));

        var ex = Assert.Throws<PilotValidationException>(() => _workflow.ReadJudgments());

        Assert.Contains(ex.Errors, e => e.Contains("outside 0..3"));
    }

    [Fact]
    public void AnnotatorsThatAreNotAttestedHuman_CannotImportQueries()
    {
        Directory.CreateDirectory(_dataset);
        var rows = Csv.Write([["id", "text", "category", "language", "written_by"], ["pilot-000", "some long enough query text", "keyword", "en", "bot"]]);
        Annotator[] people = [new() { Id = "bot", Kind = "llm", Roles = ["query-writer", "annotator"] }, new() { Id = "x", Kind = "human", Roles = ["annotator"] }];

        var ex = Assert.Throws<PilotValidationException>(() => _workflow.ImportQueries(rows, people));

        Assert.Contains(ex.Errors, e => e.Contains("must come from a human"));
    }
}
