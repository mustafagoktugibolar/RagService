using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rag.Core.Extensions;
using Rag.Core.Options;
using Rag.Core.Reranking;
using Rag.Core.Services;

namespace Rag.Core.Tests.Reranking;

/// <summary>
/// Hosts (API, QueryWorker, IndexWorker) call AddRagCore and are configured through environment variables in docker compose. These tests use
/// that exact path — real registrations, environment-variable configuration — to show that the default is Dense without reranking and that the
/// optional OpenAI Decisions provider turns on by configuration alone. No network call is made: construction does not connect.
/// </summary>
[Collection("EnvironmentVariables")]
public sealed class HostConfigurationTests : IDisposable
{
    private const string Prefix = "RAGHOSTTEST_";
    private readonly List<string> _variables = [];

    public void Dispose()
    {
        foreach (var name in _variables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private ServiceProvider Host(params (string Name, string Value)[] environment)
    {
        foreach (var (name, value) in environment.Concat(
        [
            ("QDRANT__HOST", "localhost"),
            ("OPENAI__APIKEY", "sk-test-not-real"),
            ("REDIS__CONNECTIONSTRING", "localhost:6379,abortConnect=false")
        ]))
        {
            Environment.SetEnvironmentVariable(Prefix + name, value);
            _variables.Add(Prefix + name);
        }

        var configuration = new ConfigurationBuilder().AddEnvironmentVariables(Prefix).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRagCore(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void WithNoRerankingConfiguration_TheHostIsDense_WithoutAReranker()
    {
        using var host = Host();

        var options = host.GetRequiredService<IOptions<RetrievalOptions>>().Value;

        Assert.Equal(RetrievalStrategy.Dense, options.Strategy);
        Assert.False(options.Reranking.Enabled);
        Assert.Null(host.GetService<IReranker>());
        Assert.NotNull(host.GetRequiredService<VectorSearchService>());   // the query path resolves without any reranking setup
    }

    [Fact]
    public void ExplicitlyDisabledReranking_StaysOff_EvenWithAProviderNamed()
    {
        using var host = Host(("RETRIEVAL__RERANKING__ENABLED", "false"), ("RETRIEVAL__RERANKING__PROVIDER", "OpenAiDecisions"));

        Assert.Null(host.GetService<IReranker>());
        Assert.False(host.GetRequiredService<IOptions<RetrievalOptions>>().Value.Reranking.Enabled);
    }

    [Fact]
    public void OpenAiDecisions_TurnsOnByEnvironmentConfigurationAlone_AndReusesTheOpenAiKey()
    {
        using var host = Host(
            ("RETRIEVAL__RERANKING__ENABLED", "true"),
            ("RETRIEVAL__RERANKING__PROVIDER", "OpenAiDecisions"),
            ("RETRIEVAL__RERANKING__CANDIDATEBUDGET", "20"));

        var options = host.GetRequiredService<IOptions<RetrievalOptions>>().Value;   // runs startup validation

        Assert.True(options.Reranking.Enabled);
        Assert.Equal(RetrievalStrategy.Dense, options.Strategy);                      // reranking does not change the strategy
        var reranker = Assert.IsType<OpenAiDecisionsReranker>(host.GetRequiredService<IReranker>());
        Assert.Equal("openai-decisions:gpt-6-luna:rel-v2:score", reranker.Name);
        Assert.NotNull(host.GetRequiredService<VectorSearchService>());               // the query path resolves with the reranker in place
    }

    [Fact]
    public void AMisconfiguredHost_RefusesToStart_InsteadOfRunningWithoutTheStageItWasToldToUse()
    {
        using var host = Host(
            ("RETRIEVAL__RERANKING__ENABLED", "true"),
            ("RETRIEVAL__RERANKING__PROVIDER", "Jev"));   // no JEV__APIKEY

        var failure = Assert.Throws<OptionsValidationException>(() => host.GetRequiredService<IOptions<RetrievalOptions>>().Value);

        Assert.Contains("Jev:ApiKey", failure.Message);
        Assert.DoesNotContain("sk-test-not-real", failure.Message);
    }

    [Fact]
    public void TheStrategyIsIndependentOfReranking_SoHybridWithRerankingIsJustTwoSettings()
    {
        using var host = Host(
            ("RETRIEVAL__STRATEGY", "Hybrid"),
            ("RETRIEVAL__RERANKING__ENABLED", "true"),
            ("RETRIEVAL__RERANKING__PROVIDER", "OpenAiDecisions"));

        var options = host.GetRequiredService<IOptions<RetrievalOptions>>().Value;

        Assert.Equal(RetrievalStrategy.Hybrid, options.Strategy);
        Assert.IsType<OpenAiDecisionsReranker>(host.GetRequiredService<IReranker>());
    }

    [Fact]
    public void TheShippedEnvTemplate_YieldsDenseWithoutReranking_SoCopyingItCannotSilentlyEnableAProvider()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".env.template")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var settings = File.ReadAllLines(Path.Combine(directory.FullName, ".env.template"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains('='))
            .Select(line => (Name: line[..line.IndexOf('=')], Value: line[(line.IndexOf('=') + 1)..]))
            .ToArray();

        using var host = Host(settings.Where(s => s.Name.StartsWith("RETRIEVAL__") || s.Name.StartsWith("JEV__") || s.Name.StartsWith("OPENAIDECISIONS__")).ToArray());
        var options = host.GetRequiredService<IOptions<RetrievalOptions>>().Value;

        Assert.Equal(RetrievalStrategy.Dense, options.Strategy);
        Assert.False(options.Reranking.Enabled);
        Assert.Null(host.GetService<IReranker>());
        Assert.Contains(settings, s => s.Name == "RETRIEVAL__RERANKING__ENABLED" && s.Value == "false");
    }
}

[CollectionDefinition("EnvironmentVariables", DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection;
