using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rag.Core.Extensions;
using Rag.Core.Options;

namespace Rag.Core.Tests.Rewriting;

/// <summary>What <c>ValidateOnStart</c> enforces: a misconfigured optional stage stops the host instead of failing at the first query.</summary>
public sealed class RetrievalStartupValidationTests
{
    private static ValidateOptionsResult Validate(RetrievalOptions options, string apiKey = "sk-test", string decisionsModel = "gpt-6-luna")
    {
        var validator = new RetrievalOptionsValidator(
            new ServiceCollection().BuildServiceProvider(),
            Microsoft.Extensions.Options.Options.Create(new OpenAiOptions { ApiKey = apiKey }),
            Microsoft.Extensions.Options.Options.Create(new OpenAiDecisionsOptions { Model = decisionsModel }),
            Microsoft.Extensions.Options.Options.Create(new JevOptions()));
        return validator.Validate(null, options);
    }

    private static RetrievalOptions Rewriting(Action<QueryRewritingOptions> configure)
    {
        var options = new RetrievalOptions { QueryRewriting = new QueryRewritingOptions { Enabled = true, Provider = QueryRewriteProviderKind.Rules } };
        configure(options.QueryRewriting);
        return options;
    }

    [Fact]
    public void TheDefaults_AreValid_AndNeedNoCredential() =>
        Assert.True(Validate(new RetrievalOptions(), apiKey: "").Succeeded);

    [Fact]
    public void RulesRewriting_NeedsNoCredential() =>
        Assert.True(Validate(Rewriting(_ => { }), apiKey: "").Succeeded);

    [Fact]
    public void EnabledWithoutAProvider_IsRejected() =>
        Assert.False(Validate(Rewriting(q => q.Provider = QueryRewriteProviderKind.None)).Succeeded);

    [Fact]
    public void OpenAiRewriting_RequiresAKeyAndAModel_AndNeverEchoesTheKey()
    {
        var noModel = Validate(Rewriting(q => { q.Provider = QueryRewriteProviderKind.OpenAi; q.Model = ""; }), apiKey: "sk-secret-value");
        var noKey = Validate(Rewriting(q => { q.Provider = QueryRewriteProviderKind.OpenAi; q.Model = "m"; }), apiKey: "");

        Assert.False(noModel.Succeeded);
        Assert.DoesNotContain("sk-secret-value", noModel.FailureMessage);
        Assert.Contains("Model", noModel.FailureMessage);
        Assert.False(noKey.Succeeded);
        Assert.True(Validate(Rewriting(q => { q.Provider = QueryRewriteProviderKind.OpenAi; q.Model = "m"; })).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void ATimeoutOutsideTheRange_IsRejected(int seconds) =>
        Assert.False(Validate(Rewriting(q => q.TimeoutSeconds = seconds)).Succeeded);

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void AnOverlapOutsideZeroToOne_IsRejected(double overlap) =>
        Assert.False(Validate(Rewriting(q => q.MinimumOverlap = overlap)).Succeeded);

    [Fact]
    public void ADisabledStage_IsNotValidated() =>
        Assert.True(Validate(new RetrievalOptions { QueryRewriting = new QueryRewritingOptions { Enabled = false, TimeoutSeconds = 0 } }).Succeeded);

    [Fact]
    public void RerankingAndRewritingErrors_AreReportedTogether()
    {
        var options = Rewriting(q => q.TimeoutSeconds = 0);
        options.Reranking.Enabled = true;
        options.Reranking.Provider = RerankProviderKind.OpenAiDecisions;
        options.Reranking.CandidateBudget = 1;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains("QueryRewriting:TimeoutSeconds", result.FailureMessage);
        Assert.Contains("CandidateBudget", result.FailureMessage);
    }
}
