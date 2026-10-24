using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.Core.Options;
using Rag.Core.Reranking;
using Rag.Evaluation.Dataset;
using Rag.Evaluation.Harness;
using Rag.Evaluation.Reranking;

namespace Rag.Evaluation.Cli;

/// <summary>
/// Plan and run the reranking experiment. <c>rerank-plan</c> makes no API call of any kind. <c>rerank</c> refuses to start without an explicit
/// approved request count and dollar amount that cover the plan, and stops the moment either would be exceeded. Credentials are read from the
/// git-ignored .env (or the environment) and are never printed.
/// </summary>
public static class RerankCommands
{
    public sealed record ProviderChoice(string Label, IReranker Reranker, double PricePerMillion);

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    public static ProviderChoice BuildProvider(string name, Dictionary<string, string> env, RerankExperimentSettings settings)
    {
        var rerank = new RerankingOptions
        {
            Enabled = true,
            CandidateBudget = settings.CandidateBudget,
            QuestionsPerRequest = settings.CandidateBudget,
            MaxConcurrentRequests = 1,
            TimeoutSeconds = 60,
            FailurePolicy = RerankFailurePolicy.Fail
        };
        var retrieval = Microsoft.Extensions.Options.Options.Create(new RetrievalOptions { Reranking = rerank });
        var http = new SingleClientFactory(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });

        switch (name.ToLowerInvariant())
        {
            case "openai":
                var openAiKey = env.GetValueOrDefault("OPENAI__APIKEY");
                if (string.IsNullOrWhiteSpace(openAiKey))
                {
                    throw new InvalidOperationException("OPENAI__APIKEY is empty in the env file.");
                }

                return new ProviderChoice("openai-decisions", new OpenAiDecisionsReranker(
                    http,
                    Microsoft.Extensions.Options.Options.Create(new OpenAiOptions
                    {
                        ApiKey = openAiKey,
                        Organization = env.GetValueOrDefault("OPENAI__ORGANIZATION"),
                        Project = env.GetValueOrDefault("OPENAI__PROJECT")
                    }),
                    Microsoft.Extensions.Options.Options.Create(new OpenAiDecisionsOptions()),
                    retrieval, NullLogger<OpenAiDecisionsReranker>.Instance), CostModel.OpenAiDecisionsPerMillionInput);

            case "jev":
                var jevKey = env.GetValueOrDefault("JEV__APIKEY") ?? Environment.GetEnvironmentVariable("JEV_API_KEY");
                if (string.IsNullOrWhiteSpace(jevKey))
                {
                    throw new InvalidOperationException("No Jev credential: set JEV__APIKEY in the env file or JEV_API_KEY in the environment.");
                }

                return new ProviderChoice("jev", new JevReranker(
                    http,
                    Microsoft.Extensions.Options.Options.Create(new JevOptions { ApiKey = jevKey, Model = env.GetValueOrDefault("JEV__MODEL") ?? "jev-latest" }),
                    retrieval, NullLogger<JevReranker>.Instance), CostModel.JevPerMillionInput);

            default:
                throw new ArgumentException($"Unknown --provider '{name}'. Use openai or jev.");
        }
    }

    /// <summary>What a run would really send: scored queries only, dense candidates first, then hybrid candidates not already covered or cached.</summary>
    public sealed record Plan(int Queries, int UniquePairs, int AlreadyCached, int Requests, long EstimatedInputTokens, int HybridOnlyPairs, int QueriesWhereHybridAddsCandidates);

    public static async Task<Plan> ComputePlanAsync(
        EvalDataset dataset, DenseBaselineHarness harness, string rerankerName, string cacheDirectory,
        RerankExperimentSettings settings, CancellationToken ct)
    {
        var scored = dataset.Queries.Where(q => q.RelevantDocuments.Count > 0).ToList();
        var dense = harness.RetrieverFor(new RetrievalOptions { Strategy = RetrievalStrategy.Dense });
        var hybrid = harness.RetrieverFor(new RetrievalOptions
        {
            Strategy = RetrievalStrategy.Hybrid,
            LexicalWeight = settings.Hybrid.LexicalWeight,
            RrfK = settings.Hybrid.RrfK,
            CandidateK = settings.Hybrid.CandidateK
        });

        int pairs = 0, cached = 0, requests = 0, hybridOnly = 0, queriesWithNew = 0;
        long tokens = 0;
        foreach (var query in scored)
        {
            var covered = new HashSet<string>(StringComparer.Ordinal);
            var addedByHybrid = 0;
            foreach (var (retrieve, isHybrid) in new[] { (dense, false), (hybrid, true) })
            {
                var candidates = (await retrieve(query, settings.CandidateBudget, ct)).Take(settings.CandidateBudget).ToList();
                var fresh = new List<string>();
                foreach (var hit in candidates)
                {
                    if (!covered.Add(hit.ChunkId))
                    {
                        continue;
                    }

                    pairs++;
                    if (isHybrid)
                    {
                        hybridOnly++;
                        addedByHybrid++;
                    }

                    if (FileCachingReranker.IsCached(cacheDirectory, rerankerName, query.Query, hit.Text))
                    {
                        cached++;
                    }
                    else
                    {
                        fresh.Add(hit.Text);
                    }
                }

                if (fresh.Count > 0)
                {
                    requests++;
                    tokens += CostModel.EstimateInputTokens(query.Query, fresh);
                }
            }

            if (addedByHybrid > 0)
            {
                queriesWithNew++;
            }
        }

        return new Plan(scored.Count, pairs, cached, requests, tokens, hybridOnly, queriesWithNew);
    }

    public static string DescribePlan(Plan plan, string provider, double pricePerMillion, double headroom = 1.25)
    {
        var dollars = CostModel.Dollars(plan.EstimatedInputTokens, pricePerMillion);
        var sb = new StringBuilder();
        sb.AppendLine($"Plan for {provider} (no API call was made to produce this):");
        sb.AppendLine($"  scored queries            : {plan.Queries}");
        sb.AppendLine($"  unique (query, passage) pairs: {plan.UniquePairs}  (already cached: {plan.AlreadyCached})");
        sb.AppendLine($"  candidates only hybrid adds : {plan.HybridOnlyPairs} pairs in {plan.QueriesWhereHybridAddsCandidates} of {plan.Queries} queries");
        sb.AppendLine($"  provider requests         : {plan.Requests}  (one batched request per pipeline pass that has unscored candidates)");
        sb.AppendLine($"  estimated input tokens    : {plan.EstimatedInputTokens:N0}  (~4 chars/token + rubric; observed usage will replace this)");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  estimated cost            : ${dollars:0.0000}  (price ${pricePerMillion}/1M input tokens, output free per provider docs)"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"  suggested approval        : --approve-requests {(int)Math.Ceiling(plan.Requests * headroom)} --approve-usd {Math.Max(0.01, Math.Ceiling(dollars * headroom * 1000) / 1000):0.###}  ({headroom - 1:P0} headroom)"));
        return sb.ToString();
    }
}
