using Rag.Evaluation.Dataset;

namespace Rag.Evaluation.Tests;

public sealed class DatasetLoaderTests
{
    private static EvalDataset Valid => new()
    {
        DatasetVersion = "t1",
        Name = "t",
        Corpus =
        [
            new CorpusDocument { DocumentId = "a", Text = "Alpha bravo\ncharlie" },
            new CorpusDocument { DocumentId = "b", Text = "Delta echo" }
        ],
        Queries =
        [
            new EvalQuery
            {
                Id = "q1", Query = "alpha",
                RelevantDocuments = [new RelevantDocument { DocumentId = "a", Grade = 3, Evidence = "bravo charlie" }]
            },
            new EvalQuery { Id = "q2", Query = "nothing relevant", Category = "no-answer" }
        ]
    };

    [Fact]
    public void ValidDataset_PassesAndEvidenceMatchesAcrossLineWrapsAndCase() =>
        DatasetLoader.Validate(Valid with
        {
            Queries = [Valid.Queries[0] with { RelevantDocuments = [new RelevantDocument { DocumentId = "a", Evidence = "BRAVO   Charlie" }] }]
        });

    [Fact]
    public void NoAnswerQuery_WithNoJudgments_IsValid() => DatasetLoader.Validate(Valid);

    [Fact]
    public void AllProblemsAreReportedTogether()
    {
        var broken = Valid with
        {
            DatasetVersion = "",
            Corpus = [Valid.Corpus[0], Valid.Corpus[0]],
            Queries =
            [
                new EvalQuery
                {
                    Id = "q1", Query = "x",
                    RelevantDocuments =
                    [
                        new RelevantDocument { DocumentId = "ghost" },
                        new RelevantDocument { DocumentId = "a", Grade = 9 },
                        new RelevantDocument { DocumentId = "a", Evidence = "not in the text" }
                    ]
                },
                new EvalQuery { Id = "q1", Query = "" }
            ]
        };

        var ex = Assert.Throws<DatasetValidationException>(() => DatasetLoader.Validate(broken));

        Assert.Contains(ex.Errors, e => e.Contains("datasetVersion"));
        Assert.Contains(ex.Errors, e => e.Contains("Duplicate corpus documentId 'a'"));
        Assert.Contains(ex.Errors, e => e.Contains("unknown document 'ghost'"));
        Assert.Contains(ex.Errors, e => e.Contains("grade must be 1..3"));
        Assert.Contains(ex.Errors, e => e.Contains("twice"));
        Assert.Contains(ex.Errors, e => e.Contains("evidence 'not in the text'"));
        Assert.Contains(ex.Errors, e => e.Contains("empty or duplicated"));
        Assert.Contains(ex.Errors, e => e.Contains("has no text"));
    }

    [Fact]
    public void Parse_ReadsCamelCaseJsonWithCommentsAndTrailingCommas()
    {
        var dataset = DatasetLoader.Parse("""
            {
              // comments are allowed
              "datasetVersion": "j1", "name": "json",
              "corpus": [ { "documentId": "a", "text": "hello world", }, ],
              "queries": [ { "id": "q", "query": "hello",
                             "relevantDocuments": [ { "documentId": "a", "grade": 2, "evidence": "hello" } ] } ]
            }
            """);

        Assert.Equal("j1", dataset.DatasetVersion);
        Assert.Equal(2, dataset.Queries[0].RelevantDocuments[0].Grade);
    }

    [Fact]
    public void UnknownSplit_IsRejected()
    {
        var ex = Assert.Throws<DatasetValidationException>(() => DatasetLoader.Validate(Valid with
        {
            Queries = [Valid.Queries[1] with { Split = "dev" }]
        }));

        Assert.Contains(ex.Errors, e => e.Contains("unknown split 'dev'"));
    }
}
