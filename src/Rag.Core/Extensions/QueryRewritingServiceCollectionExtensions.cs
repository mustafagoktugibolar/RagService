using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Core.Options;
using Rag.Core.Rewriting;

namespace Rag.Core.Extensions;

public static class QueryRewritingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the optional rewriting stage. Nothing is registered, and no credential is needed, unless
    /// <c>Retrieval:QueryRewriting:Enabled</c> is true. A host that already registered an <see cref="IChatClient"/> keeps it (TryAdd), which is
    /// how another model provider is plugged in without code changes in the stage.
    /// </summary>
    public static IServiceCollection AddQueryRewriting(this IServiceCollection services, IConfiguration configuration)
    {
        var rewriting = configuration.GetSection("Retrieval:QueryRewriting").Get<QueryRewritingOptions>() ?? new QueryRewritingOptions();
        if (!rewriting.Enabled || rewriting.Provider == QueryRewriteProviderKind.None)
        {
            return services;
        }

        switch (rewriting.Provider)
        {
            case QueryRewriteProviderKind.Rules:
                services.AddSingleton<IQueryRewriter, RuleBasedQueryRewriter>();
                break;

            case QueryRewriteProviderKind.OpenAi:
                services.TryAddSingletonChatClient();
                services.AddSingleton<IQueryRewriter>(sp =>
                    new ChatQueryRewriter(sp.GetRequiredService<IChatClient>(), sp.GetRequiredService<IOptions<RetrievalOptions>>().Value.QueryRewriting));
                break;
        }

        services.AddSingleton(sp => new QueryRewritingStage(
            sp.GetRequiredService<IQueryRewriter>(),
            sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<IOptions<RetrievalOptions>>().Value.QueryRewriting,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<QueryRewritingStage>()));
        return services;
    }

    private static void TryAddSingletonChatClient(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(IChatClient)))
        {
            return;
        }

        services.AddSingleton<IChatClient>(sp => ChatQueryRewriter.CreateOpenAiClient(
            sp.GetRequiredService<IOptions<OpenAiOptions>>().Value,
            sp.GetRequiredService<IOptions<RetrievalOptions>>().Value.QueryRewriting.Model));
    }
}
