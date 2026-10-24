using Rag.Evaluation.Human;

namespace Rag.Evaluation.Tests.Human;

public sealed class PilotReportClassificationTests
{
    [Theory]
    [InlineData(0.80, 0.80, "pass")]
    [InlineData(0.60, 0.60, "pass")]          // exactly at the target passes
    [InlineData(0.599, 0.80, "review")]
    [InlineData(0.40, 0.80, "review")]        // exactly at the floor is still review, not fail
    [InlineData(0.399, 0.80, "FAIL")]
    [InlineData(0.80, 0.55, "review")]        // good graded agreement but poor relevant/not-relevant agreement
    [InlineData(-0.2, 0.9, "FAIL")]
    public void Kappa_IsClassifiedAgainstTheTargetAndTheFloor(double weighted, double binary, string expected) =>
        Assert.Equal(expected, PilotReport.ClassifyKappa(weighted, binary));

    [Fact]
    public void UndefinedKappa_IsAFailure_NotAPass()
    {
        Assert.Equal("FAIL", PilotReport.ClassifyKappa(null, 0.9));
        Assert.Equal("review", PilotReport.ClassifyKappa(0.7, null));
    }

    [Theory]
    [InlineData(0.80, 0.40, "pass")]
    [InlineData(0.90, 0.30, "pass")]          // boundaries are inclusive
    [InlineData(0.90, 0.29, "review")]        // low mean but nearly all queries are easy
    [InlineData(0.93, 0.50, "review")]
    [InlineData(0.95, 0.50, "FAIL")]          // ceiling: a 0.05 gain cannot be measured
    [InlineData(0.99, 0.00, "FAIL")]
    public void Headroom_RequiresRoomToMeasureTheTargetGain(double mean, double share, string expected) =>
        Assert.Equal(expected, PilotReport.ClassifyHeadroom(mean, share));

    [Theory]
    [InlineData(30.0, "pass")]
    [InlineData(45.0, "pass")]
    [InlineData(46.0, "review")]
    [InlineData(60.0, "review")]
    [InlineData(61.0, "FAIL")]
    public void Effort_IsClassifiedByMedianSecondsPerJudgment(double seconds, string expected) =>
        Assert.Equal(expected, PilotReport.ClassifyEffort(seconds));

    [Fact]
    public void MissingTimings_AreReviewNotPass() => Assert.Equal("review", PilotReport.ClassifyEffort(null));

    [Fact]
    public void TheProtocolDocumentStatesTheSameThresholdsAsTheCode()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "eval", "protocol-human-pilot.md")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var protocol = File.ReadAllText(Path.Combine(dir.FullName, "docs", "eval", "protocol-human-pilot.md"));

        // culture- and spacing-insensitive: "98 %", "98%" and "%98" are the same threshold
        string Norm(string text) => text.Replace(" ", string.Empty);
        string Pct(double v) => Norm((v * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture)) + "%";
        string Num(double v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var text = Norm(protocol);

        foreach (var expected in new[]
        {
            Num(PilotReport.KappaTarget), Num(PilotReport.KappaFloor), Pct(PilotReport.MinimumCompleteness), Pct(PilotReport.MinimumAnswerable),
            Num(PilotReport.HeadroomMeanCeiling), Num(PilotReport.HeadroomFailMean), Pct(PilotReport.HeadroomMinimumShareBelow),
            $"{PilotReport.MedianSecondsPass:0}s", $"{PilotReport.MedianSecondsFail:0}s", $"{PilotReport.PoolMedianMin}-{PilotReport.PoolMedianMax}",
            Num(PilotReport.DeltaOfInterest)
        })
        {
            Assert.Contains(expected, text);
        }
    }
}

public sealed class PilotReportScenarioTests
{
    private static readonly Annotator[] People =
    [
        new() { Id = "owner", Kind = "human", Roles = ["query-writer", "annotator", "adjudicator"] },
        new() { Id = "colleague", Kind = "human", Roles = ["query-writer", "annotator"] }
    ];

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
                list.Add(new PilotQuery { Id = $"pilot-{n:D3}", Text = $"question number {n} about {category} topic{n}", Category = category, Language = "en", WrittenBy = n % 2 == 0 ? "owner" : "colleague" });
            }
        }

        return list;
    }

    /// <summary>
    /// 24 queries, 18 pooled passages each. Grades: k=0 -> 3, k=1 -> 2, k=2 -> 1, rest 0 (no-answer queries: only a grade-1 passage).
    /// "good" ranks the relevant passages first except for every third query, where it buries them; "worse" ranks everything backwards.
    /// </summary>
    private sealed record Scenario(PilotInputs Inputs)
    {
        public static Scenario Build(
            bool signed = true, bool buryNothing = false, double seconds = 20, bool timings = true, bool disagree = false)
        {
            var queries = Queries();
            var chunks = Enumerable.Range(0, 40).Select(i => new PilotChunk { ChunkId = $"d{i}:000000", DocumentId = $"d{i}", Title = $"T{i}", Language = "en", Text = $"passage {i}" }).ToList();
            var pools = new Dictionary<string, IReadOnlyList<PoolItem>>();
            var rankings = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>();
            var gradesA = new List<GradedItem>();
            var gradesB = new List<GradedItem>();

            for (var n = 0; n < queries.Count; n++)
            {
                var q = queries[n];
                var ids = Enumerable.Range(0, 18).Select(k => chunks[(n * 7 + k) % 40].ChunkId).ToList();
                pools[q.Id] = ids.Select(id => new PoolItem(id, [])).ToList();
                var noAnswer = q.Category == PilotCategories.NoAnswer;
                int GradeAt(int k) => noAnswer ? (k == 2 ? 1 : 0) : k switch { 0 => 3, 1 => 2, 2 => 1, _ => 0 };

                var good = buryNothing || n % 3 != 0 ? ids.ToList() : ids.AsEnumerable().Reverse().ToList();
                rankings[q.Id] = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["good"] = good.Take(10).ToList(),
                    ["worse"] = ids.AsEnumerable().Reverse().Take(10).ToList()
                };

                for (var k = 0; k < ids.Count; k++)
                {
                    gradesA.Add(new GradedItem { QueryId = q.Id, ChunkId = ids[k], Grade = GradeAt(k), Seconds = timings ? seconds : null });
                    gradesB.Add(new GradedItem
                    {
                        QueryId = q.Id, ChunkId = ids[k], Seconds = timings ? seconds : null,
                        Grade = disagree && (n + k) % 2 == 0 ? (GradeAt(k) + 2) % 4 : GradeAt(k)    // systematic disagreement
                    });
                }
            }

            var manifest = new CorpusManifest
            {
                CorpusVersion = "test", Chunking = new ChunkingSpec(250, 40, "m"), Files = [new CorpusFile("a.md", "a", "H", 1, "en", "doc")], ChunkCount = 40,
                Safety = new SafetyReview("clean", 0, null)
            };
            if (signed)
            {
                manifest = CorpusBuilder.Sign(manifest, "owner", DateTimeOffset.UtcNow);
            }

            var a = new AnnotatorJudgments { AnnotatorId = "owner", GuidelinesVersion = "g1", Items = gradesA };
            var b = new AnnotatorJudgments { AnnotatorId = "colleague", GuidelinesVersion = "g1", Items = gradesB };
            var pool = new PoolKey { CorpusVersion = "test", Systems = ["good", "worse"], Pools = pools, Rankings = rankings };

            // every disagreement is adjudicated to annotator A's grade, as a human would after discussion
            var adjudications = FinalJudgments.Disagreements(a, b)
                .Select(d => new Adjudication { QueryId = d.QueryId, ChunkId = d.ChunkId, Grade = d.GradeA, AdjudicatorId = "owner", Note = "discussed" }).ToList();
            var final = FinalJudgments.Assemble(a, b, adjudications).Final;
            return new Scenario(new PilotInputs(manifest, People, queries, chunks, pool, a, b, final, []));
        }
    }

    private static string Status(PilotReportData r, string id) => r.Criteria.Single(c => c.Id == id).Status;

    [Fact]
    public void AWellRunPilot_IsAGo_WithEveryCriterionPassing()
    {
        var report = PilotReport.Build(Scenario.Build().Inputs);

        Assert.All(report.Criteria, c => Assert.True(c.Status == "pass", $"{c.Id} {c.Status}: {c.Observed}"));
        Assert.StartsWith("GO", report.Decision);
        Assert.Equal(1.0, report.WeightedKappa!.Value, 9);
        Assert.Equal(18.0, report.MedianPoolSize);
        Assert.Equal(20.0, report.MedianSecondsPerItem);
        Assert.Equal("good", report.Headroom.MaxBy(h => h.MeanNdcg10)!.System);
    }

    [Fact]
    public void ExtrapolatedEffort_IsMedianSecondsTimesFinalQueriesTimesPoolTimesTwoAnnotators()
    {
        var report = PilotReport.Build(Scenario.Build(seconds: 20).Inputs);

        // 20 s x (160 + 30) queries x 18 passages x 2 annotators / 3600
        Assert.Equal(20.0 * 190 * 18 * 2 / 3600.0, report.ExtrapolatedAnnotatorHours!.Value, 9);
    }

    [Fact]
    public void AnUnsignedCorpus_IsANoGo_EvenIfEverythingElseIsPerfect()
    {
        var report = PilotReport.Build(Scenario.Build(signed: false).Inputs);

        Assert.Equal("FAIL", Status(report, "P1"));
        Assert.StartsWith("NO-GO", report.Decision);
    }

    [Fact]
    public void ACorpusEditedAfterSignOff_InvalidatesTheSignOff()
    {
        var scenario = Scenario.Build();
        var edited = scenario.Inputs with { Manifest = scenario.Inputs.Manifest with { Files = [scenario.Inputs.Manifest.Files[0] with { Sha256 = "CHANGED" }] } };

        Assert.Equal("FAIL", Status(PilotReport.Build(edited), "P1"));
    }

    [Fact]
    public void ACeilingCorpus_IsRejected_BecauseADelta0_05CannotBeMeasuredOnIt()
    {
        var report = PilotReport.Build(Scenario.Build(buryNothing: true).Inputs);

        Assert.Equal("FAIL", Status(report, "P7"));
        Assert.StartsWith("NO-GO", report.Decision);
    }

    [Fact]
    public void SlowAnnotation_IsAReviewNotAFailure_AndMissingTimingsAreAlsoReview()
    {
        Assert.Equal("review", Status(PilotReport.Build(Scenario.Build(seconds: 50).Inputs), "P8"));
        var noTimings = PilotReport.Build(Scenario.Build(timings: false).Inputs);
        Assert.Equal("review", Status(noTimings, "P8"));
        Assert.StartsWith("REVISE", noTimings.Decision);
        Assert.Null(noTimings.ExtrapolatedAnnotatorHours);
    }

    [Fact]
    public void AnnotatorsWhoDisagreeSystematically_FailTheAgreementCriterion()
    {
        var report = PilotReport.Build(Scenario.Build(disagree: true).Inputs);

        Assert.True(report.WeightedKappa < 0.4, $"kappa {report.WeightedKappa}");
        Assert.Equal("FAIL", Status(report, "P5"));
        Assert.True(report.Disagreements > 100);
    }

    [Fact]
    public void ANoAnswerQueryWithARelevantPassage_FailsAnswerability()
    {
        var scenario = Scenario.Build();
        var noAnswer = scenario.Inputs.Queries.First(q => q.Category == PilotCategories.NoAnswer);
        var poisoned = scenario.Inputs.Final.Select(f => f.QueryId == noAnswer.Id && f.Grade == 1 ? f with { Grade = 3 } : f).ToList();

        var report = PilotReport.Build(scenario.Inputs with { Final = poisoned });

        Assert.Equal("FAIL", Status(report, "P6"));
    }

    [Fact]
    public void AnOverlapWithAnotherQuerySet_FailsIsolation()
    {
        var scenario = Scenario.Build();

        var report = PilotReport.Build(scenario.Inputs with { Overlaps = [new SplitIsolation.Overlap("pilot-001", "golden-x", 1.0)] });

        Assert.Equal("FAIL", Status(report, "P3"));
    }

    [Fact]
    public void ThePoolSizeCriterion_IsReviewWhenTooSmallOrTooLarge()
    {
        var scenario = Scenario.Build();
        var tiny = scenario.Inputs.Pool with { Pools = scenario.Inputs.Pool.Pools.ToDictionary(p => p.Key, p => (IReadOnlyList<PoolItem>)p.Value.Take(8).ToList()) };

        Assert.Equal("review", Status(PilotReport.Build(scenario.Inputs with { Pool = tiny }), "P9"));
    }

    [Fact]
    public void TheProxyVarianceAndRequiredQueries_AreReported_AndLabelledAsAProxy()
    {
        var scenario = Scenario.Build();
        var report = PilotReport.Build(scenario.Inputs);
        var markdown = PilotReport.ToMarkdown(report, scenario.Inputs);

        Assert.NotNull(report.ProxySigma);
        Assert.True(report.ProxyRequiredQueries > 0);
        Assert.Contains("This is a proxy", markdown);
        Assert.Contains("no paid call was made", markdown);
        Assert.Contains("Decision: GO", markdown);
    }
}
