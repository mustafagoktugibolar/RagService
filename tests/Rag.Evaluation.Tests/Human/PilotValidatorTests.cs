using Rag.Evaluation.Human;

namespace Rag.Evaluation.Tests.Human;

public sealed class PilotValidatorTests
{
    private static Annotator Person(string id, params string[] roles) => new() { Id = id, Kind = "human", Roles = roles };

    private static readonly Annotator[] People =
    [
        Person("owner", "query-writer", "annotator", "adjudicator"),
        Person("colleague", "query-writer", "annotator")
    ];

    /// <summary>A valid 24-query set (5/6/4/4/3/2) used as the baseline that each test then breaks in one way.</summary>
    private static List<PilotQuery> Queries()
    {
        var plan = new (string Category, int Count)[]
        {
            (PilotCategories.Keyword, 5), (PilotCategories.Paraphrase, 6), (PilotCategories.MultiRelevant, 4),
            (PilotCategories.Conceptual, 4), (PilotCategories.CrossLingual, 3), (PilotCategories.NoAnswer, 2)
        };
        var list = new List<PilotQuery>();
        var n = 0;
        foreach (var (category, count) in plan)
        {
            for (var i = 0; i < count; i++, n++)
            {
                list.Add(new PilotQuery
                {
                    Id = $"pilot-{n:D3}", Text = $"question number {n} about topic {category} words{n}", Category = category,
                    Language = n % 4 == 0 ? "tr" : "en", WrittenBy = n % 2 == 0 ? "owner" : "colleague"
                });
            }
        }

        return list;
    }

    [Fact]
    public void AValidQuerySet_Passes() => PilotValidator.ValidateQueries(Queries(), People);

    [Theory]
    [InlineData("llm")]
    [InlineData("ai")]
    [InlineData("model")]
    [InlineData("")]
    [InlineData("Human")]   // the attestation is exact, not approximate
    public void AnAnnotatorThatIsNotAttestedHuman_IsRejected(string kind)
    {
        var people = new[] { Person("owner", "annotator", "query-writer"), new Annotator { Id = "x", Kind = kind, Roles = ["annotator"] } };

        var ex = Assert.Throws<PilotValidationException>(() => PilotValidator.ValidateAnnotators(people));

        Assert.Contains(ex.Errors, e => e.Contains("must come from a human"));
    }

    [Fact]
    public void TwoAnnotatorsAreRequired_UnlessTheDocumentedFallbackIsChosen()
    {
        Annotator[] single = [Person("owner", "annotator", "query-writer")];

        Assert.Throws<PilotValidationException>(() => PilotValidator.ValidateAnnotators(single));
        PilotValidator.ValidateAnnotators(single, allowSingleAnnotator: true);
    }

    [Fact]
    public void DuplicateAnnotatorIdsAndUnknownRoles_AreRejected()
    {
        var ex = Assert.Throws<PilotValidationException>(() => PilotValidator.ValidateAnnotators(
            [Person("a", "annotator"), Person("a", "annotator"), Person("b", "reviewer")]));

        Assert.Contains(ex.Errors, e => e.Contains("unique"));
        Assert.Contains(ex.Errors, e => e.Contains("roles"));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(31)]
    public void QueryCountOutsideTwentyToThirty_IsRejected(int count)
    {
        var queries = Queries();
        while (queries.Count > count) { queries.RemoveAt(queries.Count - 1); }
        while (queries.Count < count) { queries.Add(queries[0] with { Id = $"pilot-x{queries.Count}", Text = $"extra distinct query {queries.Count} blah" }); }

        var ex = Assert.Throws<PilotValidationException>(() => PilotValidator.ValidateQueries(queries, People));

        Assert.Contains(ex.Errors, e => e.Contains("20-30"));
    }

    [Fact]
    public void ACategoryBelowItsMinimum_IsRejected()
    {
        var queries = Queries().Where(q => q.Category != PilotCategories.NoAnswer).ToList();
        queries.AddRange(Enumerable.Range(0, 3).Select(i => new PilotQuery { Id = $"pilot-k{i}", Text = $"keyword filler query {i} zzz", Category = PilotCategories.Keyword, Language = "en", WrittenBy = "owner" }));

        var ex = Assert.Throws<PilotValidationException>(() => PilotValidator.ValidateQueries(queries, People));

        Assert.Contains(ex.Errors, e => e.Contains("no-answer") && e.Contains("at least 2"));
    }

    [Fact]
    public void AQueryNotWrittenByARegisteredHuman_IsRejected()
    {
        var queries = Queries();
        queries[0] = queries[0] with { WrittenBy = "gpt" };

        var ex = Assert.Throws<PilotValidationException>(() => PilotValidator.ValidateQueries(queries, People));

        Assert.Contains(ex.Errors, e => e.Contains("not a registered human query-writer"));
    }

    [Fact]
    public void PilotIdsMustCarryThePilotPrefix_SoTheyCannotBeConfusedWithFinalQueries()
    {
        var queries = Queries();
        queries[0] = queries[0] with { Id = "final-001" };

        Assert.Contains(Assert.Throws<PilotValidationException>(() => PilotValidator.ValidateQueries(queries, People)).Errors, e => e.Contains("pilot-"));
    }

    [Fact]
    public void QueriesThatDifferOnlyByCaseAccentsAndPunctuation_CountAsDuplicates()
    {
        var queries = Queries();
        queries[1] = queries[1] with { Text = queries[0].Text.ToUpperInvariant() + "?!" };

        Assert.Contains(Assert.Throws<PilotValidationException>(() => PilotValidator.ValidateQueries(queries, People)).Errors, e => e.Contains("same text"));
    }

    [Fact]
    public void BadLanguageAndLength_AreReported()
    {
        var queries = Queries();
        queries[0] = queries[0] with { Language = "de" };
        queries[1] = queries[1] with { Text = "short" };

        var errors = Assert.Throws<PilotValidationException>(() => PilotValidator.ValidateQueries(queries, People)).Errors;

        Assert.Contains(errors, e => e.Contains("language"));
        Assert.Contains(errors, e => e.Contains("8-300"));
    }

    private static PoolKey Pool() => new()
    {
        Pools = new Dictionary<string, IReadOnlyList<PoolItem>>
        {
            ["pilot-000"] = [new PoolItem("a:000000", []), new PoolItem("b:000000", [])]
        }
    };

    [Fact]
    public void JudgmentsInsideThePool_ReportTheirCompleteness()
    {
        var judgments = new AnnotatorJudgments { AnnotatorId = "owner", Items = [new GradedItem { QueryId = "pilot-000", ChunkId = "a:000000", Grade = 2 }] };

        Assert.Equal(0.5, PilotValidator.ValidateJudgments(judgments, Pool()));
    }

    [Theory]
    [InlineData("pilot-000", "zzz:000000", 1, "not in the pool")]
    [InlineData("pilot-000", "a:000000", 4, "outside 0..3")]
    [InlineData("pilot-000", "a:000000", -1, "outside 0..3")]
    public void JudgmentsOutsideThePoolOrScale_AreRejected(string query, string chunk, int grade, string expected)
    {
        var judgments = new AnnotatorJudgments { Items = [new GradedItem { QueryId = query, ChunkId = chunk, Grade = grade }] };

        Assert.Contains(Assert.Throws<PilotValidationException>(() => PilotValidator.ValidateJudgments(judgments, Pool())).Errors, e => e.Contains(expected));
    }

    [Fact]
    public void DuplicateJudgmentsAndUnknownFlags_AreRejected()
    {
        var item = new GradedItem { QueryId = "pilot-000", ChunkId = "a:000000", Grade = 1, Flags = ["made-up"] };
        var judgments = new AnnotatorJudgments { Items = [item, item] };

        var errors = Assert.Throws<PilotValidationException>(() => PilotValidator.ValidateJudgments(judgments, Pool())).Errors;

        Assert.Contains(errors, e => e.Contains("Duplicate"));
        Assert.Contains(errors, e => e.Contains("unknown flag"));
    }
}

public sealed class SplitIsolationTests
{
    [Theory]
    [InlineData("What is the Retry-After header?", "what is the retry after header")]
    [InlineData("  İstanbul'da   ÇALIŞAN?  ", "istanbul da calisan")]
    public void Normalise_LowercasesStripsAccentsAndPunctuation(string input, string expected) =>
        Assert.Equal(expected, SplitIsolation.Normalise(input));

    [Fact]
    public void ExactAndNearDuplicates_AreFound_UnrelatedQueriesAreNot()
    {
        (string, string)[] pilot = [("pilot-001", "How does the circuit breaker open?"), ("pilot-002", "Why is hybrid search disabled by default?")];
        (string, string)[] other =
        [
            ("golden-x", "how does the circuit breaker open"),               // identical after normalisation
            ("golden-y", "How does the circuit breaker open again?"),        // near duplicate: Jaccard 6/7
            ("golden-z", "Where are embeddings cached?")                     // unrelated
        ];

        var overlaps = SplitIsolation.FindOverlaps(pilot, other);

        Assert.Equal(["golden-x", "golden-y"], overlaps.Select(o => o.OtherQueryId).Order());
        Assert.All(overlaps, o => Assert.Equal("pilot-001", o.PilotQueryId));
    }

    [Fact]
    public void TheThresholdDecidesHowSimilarIsTooSimilar()
    {
        (string, string)[] pilot = [("p", "circuit breaker opens after failures")];
        (string, string)[] other = [("o", "circuit breaker closes after probes")];   // Jaccard 3/7

        Assert.Empty(SplitIsolation.FindOverlaps(pilot, other, threshold: 0.8));
        Assert.Single(SplitIsolation.FindOverlaps(pilot, other, threshold: 0.4));
    }

    [Fact]
    public async Task NoPilotQueryMayEverMatchAGoldenV1Query()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "datasets", "golden-v1", "dataset.json");
        var golden = await Rag.Evaluation.Dataset.DatasetLoader.LoadAsync(path, CancellationToken.None);

        // a representative pilot-style query set must be disjoint from golden-v1; here, a sentence copied from golden-v1 must be caught
        var copied = new[] { ("pilot-777", golden.Queries[0].Query) };

        Assert.Single(SplitIsolation.FindOverlaps(copied, golden.Queries.Select(q => (q.Id, q.Query))));
    }
}
