using Rag.Evaluation.Human;

namespace Rag.Evaluation.Tests.Human;

public sealed class AdjudicationTests
{
    private static GradedItem G(string q, string c, int grade, params string[] flags) =>
        new() { QueryId = q, ChunkId = c, Grade = grade, Flags = flags };

    private static AnnotatorJudgments J(string id, params GradedItem[] items) => new() { AnnotatorId = id, Items = items };

    private static Adjudication Decision(string q, string c, int grade, string note = "reason", string who = "adjudicator") =>
        new() { QueryId = q, ChunkId = c, Grade = grade, Note = note, AdjudicatorId = who };

    private static readonly AnnotatorJudgments A = J("a", G("q1", "c1", 3), G("q1", "c2", 1), G("q1", "c3", 2, "unclear-query"), G("q1", "c4", 0), G("q2", "c1", 2));
    private static readonly AnnotatorJudgments B = J("b", G("q1", "c1", 3), G("q1", "c2", 2), G("q1", "c3", 2), G("q1", "c4", 0));

    [Fact]
    public void EqualUnflaggedGrades_AreAgreed_EverythingElseNeedsAHuman()
    {
        var result = FinalJudgments.Assemble(A, B, []);

        Assert.Equal(["c1", "c4"], result.Final.Where(f => f.Source == "agreed").Select(f => f.ChunkId));
        Assert.Equal(["c2", "c3"], result.Pending.Select(p => p.ChunkId));    // a 1-step difference and a flagged item
        Assert.Equal(1, result.GradedByOnlyOne);                              // q2/c1 was graded by A only
    }

    [Fact]
    public void OnlyAHumanDecisionResolvesADisagreement_AndItIsRecordedAsAdjudicated()
    {
        var result = FinalJudgments.Assemble(A, B, [Decision("q1", "c2", 2), Decision("q1", "c3", 1)]);

        Assert.Empty(result.Pending);
        Assert.Equal(2, result.Final.Single(f => f.ChunkId == "c2").Grade);
        Assert.Equal("adjudicated", result.Final.Single(f => f.ChunkId == "c3").Source);
        Assert.Equal(4, result.Final.Count);
    }

    [Theory]
    [InlineData(5, "reason", "adjudicator")]
    [InlineData(-1, "reason", "adjudicator")]
    [InlineData(2, "", "adjudicator")]
    [InlineData(2, "reason", "")]
    public void AnIncompleteAdjudication_IsRejected(int grade, string note, string who)
    {
        Assert.Throws<PilotValidationException>(() => FinalJudgments.Assemble(A, B, [Decision("q1", "c2", grade, note, who)]));
    }

    [Fact]
    public void TheDisagreementListIsDeterministicAndOrdered()
    {
        var list = FinalJudgments.Disagreements(A, B);

        Assert.Equal([("q1", "c2"), ("q1", "c3")], list.Select(d => (d.QueryId, d.ChunkId)));
        Assert.Equal((1, 2), (list[0].GradeA, list[0].GradeB));
    }

    [Fact]
    public void TheAdjudicationSheet_ShowsBothGrades_ButNeverAnySystem_AndRoundTripsDecisions()
    {
        var chunks = new[] { new PilotChunk { ChunkId = "c2", Title = "T", Text = "passage two" }, new PilotChunk { ChunkId = "c3", Title = "T", Text = "passage three" } };
        var queries = new[] { new PilotQuery { Id = "q1", Text = "the query" } };
        var csv = FinalJudgments.DisagreementSheet(FinalJudgments.Disagreements(A, B), queries, chunks);

        Assert.Contains("grade_a", csv);
        Assert.DoesNotContain("bm25", csv, StringComparison.OrdinalIgnoreCase);

        var rows = Csv.Read(csv);
        rows[1][9] = "2"; rows[1][10] = "A was too strict; passage names the setting";
        var parsed = FinalJudgments.ParseAdjudicationCsv(Csv.Write(rows.Select(r => (IReadOnlyList<string>)r)), "owner");

        var only = Assert.Single(parsed);
        Assert.Equal(("q1", "c2", 2, "owner"), (only.QueryId, only.ChunkId, only.Grade, only.AdjudicatorId));
        Assert.Contains("too strict", only.Note);
    }
}
