using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rag.Core.Options;
using Rag.Core.Reranking;

namespace Rag.Core.Extensions;

public static class RerankingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the selected reranker — one singleton, so its circuit breaker and concurrency limit are shared by every query.
    /// Nothing is registered (and no credential is needed) while <c>Retrieval:Reranking:Enabled</c> is false. The Onnx provider is
    /// registered by the optional Rag.Reranking.Onnx project; startup validation fails if it is selected but absent.
    /// </summary>
    public static IServiceCollection AddReranking(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OpenAiDecisionsOptions>(configuration.GetSection("OpenAIDecisions"));
        services.Configure<JevOptions>(configuration.GetSection("Jev"));
        services.AddSingleton<IValidateOptions<RetrievalOptions>, RetrievalOptionsValidator>();
        services.AddOptions<RetrievalOptions>().Bind(configuration.GetSection("Retrieval")).ValidateOnStart();

        var reranking = configuration.GetSection("Retrieval:Reranking").Get<RerankingOptions>() ?? new RerankingOptions();
        if (!reranking.Enabled)
        {
            return services;
        }

        switch (reranking.Provider)
        {
            case RerankProviderKind.None:
                services.AddSingleton<IReranker, NoOpReranker>();
                break;

            case RerankProviderKind.OpenAiDecisions:
                services.AddHttpClient(OpenAiDecisionsReranker.HttpClientName, (sp, client) =>
                    client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<RetrievalOptions>>().Value.Reranking.TimeoutSeconds));
                services.AddSingleton<OpenAiDecisionsReranker>();
                services.AddSingleton<IReranker>(sp => sp.GetRequiredService<OpenAiDecisionsReranker>());
                break;

            case RerankProviderKind.Jev:
                services.AddHttpClient(JevReranker.HttpClientName, (sp, client) =>
                    client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<RetrievalOptions>>().Value.Reranking.TimeoutSeconds));
                services.AddSingleton<JevReranker>();
                services.AddSingleton<IReranker>(sp => sp.GetRequiredService<JevReranker>());
                break;

            case RerankProviderKind.Onnx:
                break;   // AddOnnxReranker (optional project) registers it
        }

        return services;
    }
}

/// <summary>
/// Runs at host start (<c>ValidateOnStart</c>). When reranking is enabled the process refuses to start on a configuration that
/// could only fail later or unsafely: out-of-range budgets, a missing credential, a non-HTTPS endpoint, or a provider that is not registered.
/// </summary>
public sealed class RetrievalOptionsValidator(
    IServiceProvider services,
    IOptions<OpenAiOptions> openAi,
    IOptions<OpenAiDecisionsOptions> decisions,
    IOptions<JevOptions> jev) : IValidateOptions<RetrievalOptions>
{
    public ValidateOptionsResult Validate(string? name, RetrievalOptions options)
    {
        var errors = new List<string>();
        ValidateRewriting(errors, options.QueryRewriting);
        var r = options.Reranking;
        if (!r.Enabled)
        {
            return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
        }

        Range(errors, "Retrieval:Reranking:CandidateBudget", r.CandidateBudget, 2, 400);
        Range(errors, "Retrieval:Reranking:QuestionsPerRequest", r.QuestionsPerRequest, 1, 400);
        Range(errors, "Retrieval:Reranking:MaxConcurrentRequests", r.MaxConcurrentRequests, 1, 64);
        Range(errors, "Retrieval:Reranking:TimeoutSeconds", r.TimeoutSeconds, 1, 120);
        if (r.MaxPassageCharacters < 200)
        {
            errors.Add("Retrieval:Reranking:MaxPassageCharacters must be at least 200.");
        }

        switch (r.Provider)
        {
            case RerankProviderKind.OpenAiDecisions:
                Required(errors, "OpenAI:ApiKey (reused by OpenAI Decisions)", openAi.Value.ApiKey);
                Required(errors, "OpenAIDecisions:Model", decisions.Value.Model);
                SecureUrl(errors, "OpenAIDecisions:BaseUrl", decisions.Value.BaseUrl);
                break;

            case RerankProviderKind.Jev:
                Required(errors, "Jev:ApiKey", jev.Value.ApiKey);
                Required(errors, "Jev:Model", jev.Value.Model);
                SecureUrl(errors, "Jev:BaseUrl", jev.Value.BaseUrl);
                break;

            case RerankProviderKind.Onnx:
                if (services.GetService<IReranker>() is null)
                {
                    errors.Add("Provider 'Onnx' needs the Rag.Reranking.Onnx project and AddOnnxReranker(); no reranker is registered.");
                }

                break;
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private void ValidateRewriting(List<string> errors, QueryRewritingOptions q)
    {
        if (!q.Enabled)
        {
            return;
        }

        if (q.Provider == QueryRewriteProviderKind.None)
        {
            errors.Add("Retrieval:QueryRewriting:Enabled is true but Provider is None.");
            return;
        }

        Range(errors, "Retrieval:QueryRewriting:TimeoutSeconds", q.TimeoutSeconds, 1, 60);
        Range(errors, "Retrieval:QueryRewriting:MinimumWords", q.MinimumWords, 1, 50);
        Range(errors, "Retrieval:QueryRewriting:MaxOutputTokens", q.MaxOutputTokens, 8, 512);
        Range(errors, "Retrieval:QueryRewriting:MaxCharacters", q.MaxCharacters, 20, 2000);
        Range(errors, "Retrieval:QueryRewriting:CacheTtlHours", q.CacheTtlHours, 0, 24 * 30);
        if (q.MinimumOverlap is < 0 or > 1)
        {
            errors.Add("Retrieval:QueryRewriting:MinimumOverlap must be between 0 and 1.");
        }

        if (q.Provider == QueryRewriteProviderKind.OpenAi)
        {
            Required(errors, "OpenAI:ApiKey (reused by query rewriting)", openAi.Value.ApiKey);
            Required(errors, "Retrieval:QueryRewriting:Model", q.Model);
        }
    }

    private static void Range(List<string> errors, string key, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{key} must be between {min} and {max} (was {value}).");
        }
    }

    // Values are never echoed: a credential must not reach a log through its own validation message.
    private static void Required(List<string> errors, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{key} is required when reranking is enabled with this provider.");
        }
    }

    private static void SecureUrl(List<string> errors, string key, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            errors.Add($"{key} must be an absolute https URL (plain http is allowed only for loopback addresses).");
        }
    }
}
