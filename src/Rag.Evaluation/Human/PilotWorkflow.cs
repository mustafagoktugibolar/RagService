using System.Text;
using Rag.Evaluation.Dataset;

namespace Rag.Evaluation.Human;

/// <summary>
/// The pilot's steps over a dataset directory. All of it is offline and free: no model is called, no credential is read. People provide the
/// queries and the labels; these methods only freeze, validate, pool, blind, compare and report.
/// </summary>
public sealed class PilotWorkflow(string datasetDirectory)
{
    public const string GuidelinesVersion = "guidelines-v1";

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);   // Excel needs the BOM for Turkish letters

    public string Directory { get; } = datasetDirectory;

    public string Path_(params string[] parts) => System.IO.Path.Combine([Directory, .. parts]);

    public CorpusManifest ReadManifest() => PilotJson.Read<CorpusManifest>(Path_("corpus-manifest.json"));

    public IReadOnlyList<PilotChunk> ReadChunks() => PilotJson.Read<List<PilotChunk>>(Path_("chunks.json"));

    public IReadOnlyList<Annotator> ReadAnnotators() => PilotJson.Read<List<Annotator>>(Path_("annotators.json"));

    public IReadOnlyList<PilotQuery> ReadQueries() => PilotJson.Read<List<PilotQuery>>(Path_("queries.json"));

    public PoolKey ReadPool() => PilotJson.Read<PoolKey>(Path_("pools", "private", "pool-key.json"));

    public async Task<CorpusBuildResult> FreezeCorpusAsync(
        string repositoryRoot, IReadOnlyList<(string Path, string Reason)> allowList, IReadOnlyList<ExcludedFile> excluded,
        ChunkingSpec chunking, string corpusVersion, string sourceCommit, CancellationToken ct, IReadOnlyDictionary<string, string>? topicPrompts = null)
    {
        var result = await CorpusBuilder.BuildAsync(repositoryRoot, Directory, allowList, excluded, chunking, corpusVersion, sourceCommit, ct, topicPrompts);
        await File.WriteAllTextAsync(Path_("topics.md"),
            "# Topic prompts for query writers\n\n" +
            "Write the question you would ask **if you did not know the answer**. You are shown broad areas only, never passages or document titles:\n" +
            "do not look up the documents, and use your own words.\n\n" + result.Topics, ct);
        return result;
    }

    public CorpusManifest SignOff(string by, DateTimeOffset at)
    {
        var signed = CorpusBuilder.Sign(ReadManifest(), by, at);
        File.WriteAllText(Path_("corpus-manifest.json"), PilotJson.Serialize(signed));
        return signed;
    }

    public IReadOnlyList<PilotQuery> ImportQueries(string csv, IReadOnlyList<Annotator> annotators)
    {
        var rows = Csv.Read(csv);
        var columns = rows[0].Select((n, i) => (n: n.Trim().ToLowerInvariant(), i)).ToDictionary(x => x.n, x => x.i);
        string Cell(List<string> row, string name) => columns.TryGetValue(name, out var i) && i < row.Count ? row[i].Trim() : string.Empty;

        var queries = rows.Skip(1).Where(r => Cell(r, "text").Length > 0).Select(r => new PilotQuery
        {
            Id = Cell(r, "id"), Text = Cell(r, "text"), Category = Cell(r, "category"), Language = Cell(r, "language"), WrittenBy = Cell(r, "written_by")
        }).ToList();

        PilotValidator.ValidateAnnotators(annotators);
        PilotValidator.ValidateQueries(queries, annotators);
        File.WriteAllText(Path_("queries.json"), PilotJson.Serialize(queries));
        return queries;
    }

    /// <summary>
    /// Builds the unblinded pool key (kept under pools/private, never given to annotators) and one blinded CSV sheet per annotator.
    /// Refuses to run unless the corpus is signed off for sending and no query overlaps another query set.
    /// </summary>
    public (PoolKey Pool, IReadOnlyList<string> Sheets) BuildPools(
        IReadOnlyList<PoolSystem> extraSystems, IEnumerable<(string Id, string Text)> otherQuerySets, int depth = 10, int randomNegatives = 3, int seed = 20261024)
    {
        var manifest = ReadManifest();
        if (!CorpusBuilder.IsSignedOff(manifest))
        {
            throw new PilotValidationException(["The corpus has no valid owner sign-off for the current manifest. Review it and run the sign-off step first."]);
        }

        var annotators = ReadAnnotators();
        var queries = ReadQueries();
        PilotValidator.ValidateAnnotators(annotators);
        PilotValidator.ValidateQueries(queries, annotators);

        var overlaps = SplitIsolation.FindOverlaps(queries.Select(q => (q.Id, q.Text)), otherQuerySets);
        if (overlaps.Count > 0)
        {
            throw new PilotValidationException(overlaps.Select(o => $"Pilot query {o.PilotQueryId} matches {o.OtherQueryId} (similarity {o.Similarity:0.00}); the pilot must stay separate from other query sets.").ToList());
        }

        var chunks = ReadChunks();
        var systems = new List<PoolSystem> { PoolBuilder.Bm25(chunks), PoolBuilder.HashingDense(chunks) };
        systems.AddRange(extraSystems);
        var pool = PoolBuilder.Build(chunks, queries, systems, depth, randomNegatives, seed, manifest.CorpusVersion);

        System.IO.Directory.CreateDirectory(Path_("pools", "private"));
        System.IO.Directory.CreateDirectory(Path_("pools", "sheets"));
        File.WriteAllText(Path_("pools", "private", "pool-key.json"), PilotJson.Serialize(pool));

        var sheets = new List<string>();
        foreach (var annotator in annotators.Where(a => a.Roles.Contains("annotator")))
        {
            var path = Path_("pools", "sheets", $"annotator-{annotator.Id}.csv");
            File.WriteAllText(path, AnnotationSheets.ToCsv(AnnotationSheets.Build(pool, queries, chunks, annotator.Id, seed)), Utf8WithBom);
            sheets.Add(path);
        }

        return (pool, sheets);
    }

    public (AnnotatorJudgments A, AnnotatorJudgments B) ReadJudgments()
    {
        var graders = ReadAnnotators().Where(a => a.Roles.Contains("annotator")).Take(2).ToList();
        var pool = ReadPool();
        AnnotatorJudgments Load(Annotator person)
        {
            var judgments = AnnotationSheets.ParseCsv(File.ReadAllText(Path_("judgments", $"annotator-{person.Id}.csv")), person.Id, GuidelinesVersion);
            PilotValidator.ValidateJudgments(judgments, pool);
            return judgments;
        }

        return (Load(graders[0]), Load(graders[1]));
    }

    public string WriteDisagreementSheet()
    {
        var (a, b) = ReadJudgments();
        var sheet = FinalJudgments.DisagreementSheet(FinalJudgments.Disagreements(a, b), ReadQueries(), ReadChunks());
        System.IO.Directory.CreateDirectory(Path_("adjudication"));
        var path = Path_("adjudication", "disagreements.csv");
        File.WriteAllText(path, sheet, Utf8WithBom);
        return path;
    }

    public (PilotReportData Data, PilotInputs Inputs, string Markdown) BuildReport(IEnumerable<(string Id, string Text)> otherQuerySets, int plannedScored = 160, int plannedNoAnswer = 30)
    {
        var annotators = ReadAnnotators();
        var queries = ReadQueries();
        var (a, b) = ReadJudgments();

        var adjudicator = annotators.FirstOrDefault(x => x.Roles.Contains("adjudicator"))
            ?? throw new PilotValidationException(["No adjudicator is registered."]);
        var adjudicationPath = Path_("adjudication", "adjudicated.csv");
        var adjudications = File.Exists(adjudicationPath) ? FinalJudgments.ParseAdjudicationCsv(File.ReadAllText(adjudicationPath), adjudicator.Id) : [];

        var assembled = FinalJudgments.Assemble(a, b, adjudications);
        if (assembled.Pending.Count > 0)
        {
            throw new PilotValidationException([$"{assembled.Pending.Count} disagreement(s) still need a human adjudication (see adjudication/disagreements.csv); no final labels were written."]);
        }

        File.WriteAllText(Path_("final-judgments.json"), PilotJson.Serialize(assembled.Final));
        var overlaps = SplitIsolation.FindOverlaps(queries.Select(q => (q.Id, q.Text)), otherQuerySets);
        var inputs = new PilotInputs(ReadManifest(), annotators, queries, ReadChunks(), ReadPool(), a, b, assembled.Final, overlaps, plannedScored, plannedNoAnswer);
        var data = PilotReport.Build(inputs);
        return (data, inputs, PilotReport.ToMarkdown(data, inputs));
    }

    public static IEnumerable<(string Id, string Text)> GoldenQueries(EvalDataset golden) => golden.Queries.Select(q => (q.Id, q.Query));
}
