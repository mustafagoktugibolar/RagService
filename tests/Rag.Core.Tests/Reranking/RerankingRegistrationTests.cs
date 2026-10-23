using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Core.Extensions;
using Rag.Core.Options;
using Rag.Core.Reranking;

namespace Rag.Core.Tests.Reranking;

public sealed class RerankingRegistrationTests
{
    private const string OpenAiKey = "sk-test-SECRET-openai";
    private const string JevKey = "tsk-test-SECRET-jev";

    private static ServiceProvider Provider(Dictionary<string, string?> settings, Action<IServiceCollection>? extra = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<OpenAiOptions>(configuration.GetSection("OpenAI"));
        services.AddReranking(configuration);
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> Enabled(string provider, params (string Key, string? Value)[] more)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Retrieval:Reranking:Enabled"] = "true",
            ["Retrieval:Reranking:Provider"] = provider
        };
        foreach (var (key, value) in more)
        {
            settings[key] = value;
        }

        return settings;
    }

    private static OptionsValidationException StartupFailure(ServiceProvider provider) =>
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RetrievalOptions>>().Value);

    [Fact]
    public void Disabled_RegistersNoReranker_AndNeedsNoCredentials()
    {
        using var provider = Provider(new Dictionary<string, string?> { ["Retrieval:Reranking:Provider"] = "Jev" });

        Assert.Null(provider.GetService<IReranker>());
        Assert.False(provider.GetRequiredService<IOptions<RetrievalOptions>>().Value.Reranking.Enabled);
    }

    [Fact]
    public void Defaults_AreDenseWithRerankingOff()
    {
        using var provider = Provider([]);

        var options = provider.GetRequiredService<IOptions<RetrievalOptions>>().Value;

        Assert.Equal(RetrievalStrategy.Dense, options.Strategy);
        Assert.False(options.Reranking.Enabled);
        Assert.Equal(RerankFailurePolicy.Fallback, options.Reranking.FailurePolicy);
        Assert.Equal(RelevanceMechanism.Score, options.Reranking.Mechanism);
    }

    [Fact]
    public void OpenAiDecisions_IsSelectedByConfiguration_AndIsOneSharedInstance()
    {
        using var provider = Provider(Enabled("OpenAiDecisions", ("OpenAI:ApiKey", OpenAiKey)));

        var reranker = provider.GetRequiredService<IReranker>();

        Assert.IsType<OpenAiDecisionsReranker>(reranker);
        Assert.Same(reranker, provider.GetRequiredService<IReranker>());
        Assert.Equal("openai-decisions:gpt-6-luna:rel-v1:score", reranker.Name);
    }

    [Fact]
    public void Jev_IsSelectedByConfiguration()
    {
        using var provider = Provider(Enabled("Jev", ("Jev:ApiKey", JevKey), ("Jev:Model", "jev-1.13.0")));

        var reranker = provider.GetRequiredService<IReranker>();

        Assert.IsType<JevReranker>(reranker);
        Assert.Equal("jev:jev-1.13.0:rel-v1:score", reranker.Name);
    }

    [Fact]
    public void None_RegistersTheNoOpReranker_EvenWhenEnabled() =>
        Assert.IsType<NoOpReranker>(Provider(Enabled("None")).GetRequiredService<IReranker>());

    [Fact]
    public void ProviderCredentialsAreNotMixedUp_OpenAiKeyDoesNotSatisfyJev()
    {
        using var provider = Provider(Enabled("Jev", ("OpenAI:ApiKey", OpenAiKey)));

        var failure = StartupFailure(provider);

        Assert.Contains("Jev:ApiKey", failure.Message);
    }

    [Theory]
    [InlineData("OpenAiDecisions", "OpenAI:ApiKey")]
    [InlineData("Jev", "Jev:ApiKey")]
    public void EnabledWithoutACredential_RefusesToStart_AndNamesTheSetting(string providerName, string expectedSetting)
    {
        using var provider = Provider(Enabled(providerName));

        Assert.Contains(expectedSetting, StartupFailure(provider).Message);
    }

    [Fact]
    public void StartupFailures_NeverEchoACredential()
    {
        using var provider = Provider(Enabled("OpenAiDecisions", ("OpenAI:ApiKey", OpenAiKey), ("OpenAIDecisions:BaseUrl", "http://evil.example.com"), ("Retrieval:Reranking:CandidateBudget", "9999")));

        var failure = StartupFailure(provider);

        Assert.DoesNotContain(OpenAiKey, failure.Message);
        Assert.Contains("CandidateBudget", failure.Message);   // both problems reported together
        Assert.Contains("BaseUrl", failure.Message);
    }

    [Theory]
    [InlineData("http://api.openai.com")]
    [InlineData("ftp://api.openai.com")]
    [InlineData("not a url")]
    public void NonHttpsEndpoints_AreRejected(string url)
    {
        using var provider = Provider(Enabled("OpenAiDecisions", ("OpenAI:ApiKey", OpenAiKey), ("OpenAIDecisions:BaseUrl", url)));

        Assert.Contains("OpenAIDecisions:BaseUrl", StartupFailure(provider).Message);
    }

    [Fact]
    public void PlainHttp_IsAllowedOnlyForLoopback_ForLocalTestDoubles()
    {
        using var provider = Provider(Enabled("OpenAiDecisions", ("OpenAI:ApiKey", OpenAiKey), ("OpenAIDecisions:BaseUrl", "http://localhost:5099")));

        Assert.NotNull(provider.GetRequiredService<IOptions<RetrievalOptions>>().Value);
    }

    [Theory]
    [InlineData("CandidateBudget", "1")]
    [InlineData("CandidateBudget", "401")]
    [InlineData("QuestionsPerRequest", "0")]
    [InlineData("QuestionsPerRequest", "401")]
    [InlineData("MaxConcurrentRequests", "0")]
    [InlineData("TimeoutSeconds", "0")]
    [InlineData("TimeoutSeconds", "121")]
    [InlineData("MaxPassageCharacters", "50")]
    public void OutOfRangeBudgets_RefuseToStart(string key, string value)
    {
        using var provider = Provider(Enabled("None", ($"Retrieval:Reranking:{key}", value)));

        Assert.Contains(key, StartupFailure(provider).Message);
    }

    [Fact]
    public void OnnxProvider_WithoutItsProjectRegistered_RefusesToStart()
    {
        using var provider = Provider(Enabled("Onnx"));

        Assert.Contains("AddOnnxReranker", StartupFailure(provider).Message);
    }

    [Fact]
    public void OnnxProvider_PassesValidation_WhenSomeRerankerIsRegistered()
    {
        using var provider = Provider(Enabled("Onnx"), services => services.AddSingleton<IReranker, NoOpReranker>());

        Assert.NotNull(provider.GetRequiredService<IOptions<RetrievalOptions>>().Value);
    }

    [Fact]
    public void ConfigurationBinds_TheNestedRetrievalRerankingSection()
    {
        using var provider = Provider(Enabled("None",
            ("Retrieval:Strategy", "Hybrid"), ("Retrieval:Reranking:CandidateBudget", "30"), ("Retrieval:Reranking:TimeoutSeconds", "9"),
            ("Retrieval:Reranking:FailurePolicy", "Fail"), ("Retrieval:Reranking:Mechanism", "Predicate")));

        var options = provider.GetRequiredService<IOptions<RetrievalOptions>>().Value;

        Assert.Equal(RetrievalStrategy.Hybrid, options.Strategy);
        Assert.Equal(30, options.Reranking.CandidateBudget);
        Assert.Equal(9, options.Reranking.TimeoutSeconds);
        Assert.Equal(RerankFailurePolicy.Fail, options.Reranking.FailurePolicy);
        Assert.Equal(RelevanceMechanism.Predicate, options.Reranking.Mechanism);
    }
}
