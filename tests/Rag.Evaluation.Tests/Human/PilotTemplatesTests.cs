using System.Text.Json;
using Rag.Evaluation.Human;

namespace Rag.Evaluation.Tests.Human;

public sealed class PilotTemplatesTests
{
    private static string PilotDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "datasets", "human-pilot-v1", "queries.template.csv")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "datasets", "human-pilot-v1");
    }

    [Fact]
    public void TheQueryTemplate_HasTwentyFourSlots_ThatSatisfyEveryCategoryMinimum_AndPilotIdsOnly()
    {
        var rows = Csv.Read(File.ReadAllText(Path.Combine(PilotDirectory(), "queries.template.csv")));
        var columns = rows[0].Select(c => c.Trim().TrimStart('﻿')).ToList();

        Assert.Equal(["id", "text", "category", "language", "written_by", "hint"], columns);
        var slots = rows.Skip(1).ToList();
        Assert.Equal(24, slots.Count);
        Assert.All(slots, r => Assert.StartsWith("pilot-", r[0]));
        Assert.Equal(slots.Count, slots.Select(r => r[0]).Distinct().Count());
        foreach (var (category, minimum) in PilotCategories.MinimumPerCategory)
        {
            Assert.True(slots.Count(r => r[2] == category) >= minimum, category);
        }

        Assert.All(slots, r => Assert.True(string.IsNullOrEmpty(r[1]), "the template must ship without any query text: queries are written by people"));
    }

    [Fact]
    public void TheAnnotatorTemplate_AttestsHumansAndHasTheRequiredRoles()
    {
        var people = JsonSerializer.Deserialize<List<Annotator>>(
            File.ReadAllText(Path.Combine(PilotDirectory(), "annotators.template.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        PilotValidator.ValidateAnnotators(people);   // two annotators, kind human, valid roles
        Assert.Contains(people, p => p.Roles.Contains("adjudicator"));
    }

    [Fact]
    public void TheDatasetReadme_StatesTheTwoRulesThatMatterMost()
    {
        var readme = File.ReadAllText(Path.Combine(PilotDirectory(), "README.md"));

        Assert.Contains("No relevance label here is produced by a model", readme);
        Assert.Contains("separate from the final held-out set", readme);
    }
}
