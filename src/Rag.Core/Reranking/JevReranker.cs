using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Core.Options;

namespace Rag.Core.Reranking;

/// <summary>
/// TypeSafe Jev adapter (<c>POST {BaseUrl}/v1/systemone</c>). The query is the shared <c>state</c>; each candidate is one
/// <c>score</c> (or <c>noul</c>) question keyed <c>c0…cN</c> whose instructions carry that passage. Jev's questions are
/// evaluated "in parallel and in isolation against the same state" (its documentation), so candidates cannot influence one
/// another. Needs its own <c>Jev:ApiKey</c>; the OpenAI key is unrelated. Jev's DTO shapes live only in this class.
/// </summary>
public sealed class JevReranker : ProviderReranker
{
    public const string HttpClientName = "rag-rerank-jev";
    public const string Provider = "jev";

    private readonly IHttpClientFactory _http;
    private readonly JevOptions _jev;

    public JevReranker(
        IHttpClientFactory http,
        IOptions<JevOptions> jev,
        IOptions<RetrievalOptions> retrieval,
        ILogger<JevReranker> logger,
        Resilience.ResiliencePipeline? pipeline = null)
        : base(retrieval.Value.Reranking, logger, Provider, jev.Value.Model, pipeline)
    {
        _http = http;
        _jev = jev.Value;
    }

    protected override async Task<BatchResponse> SendAsync(string query, RelevanceBatch batch, CancellationToken ct)
    {
        var questions = new JsonObject();
        foreach (var question in batch.Questions)
        {
            questions[question.Name] = BuildQuestion(question);
        }

        var body = new JsonObject
        {
            ["state"] = ProviderHttp.QueryInput(query),
            ["model"] = _jev.Model,
            ["questions"] = questions
        };

        var json = await ProviderHttp.PostJsonAsync(
            _http.CreateClient(HttpClientName), ProviderHttp.BuildUrl(_jev.BaseUrl, "/v1/systemone"),
            _jev.ApiKey, body, Provider, extraHeaders: null, ct);

        return Parse(json);
    }

    private JsonNode BuildQuestion(RelevanceQuestion question)
    {
        if (Mechanism == RelevanceMechanism.Predicate)
        {
            return new JsonObject
            {
                ["type"] = "noul",
                ["instructions"] = RelevanceRubric.PredicateInstructions(question.Passage),
                ["criteria"] = new JsonObject
                {
                    ["true"] = "The passage contains information that helps answer the search query.",
                    ["false"] = "The passage does not help answer the search query."
                }
            };
        }

        return new JsonObject
        {
            ["type"] = "score",
            ["instructions"] = RelevanceRubric.ScoreInstructions(question.Passage),
            ["criteria"] = new JsonArray(RelevanceRubric.Levels.Select(l => (JsonNode)l.Description).ToArray())
        };
    }

    internal BatchResponse Parse(JsonObject json)
    {
        if (json["answers"] is not JsonObject answers)
        {
            throw new InvalidProviderResponseException("The response has no 'answers' object.");
        }

        var expectedType = Mechanism == RelevanceMechanism.Score ? "score" : "noul";
        var parsed = new List<ProviderAnswer>(answers.Count);
        foreach (var (name, node) in answers)
        {
            var type = node?["type"]?.GetValue<string>();
            if (type != expectedType)
            {
                throw new InvalidProviderResponseException($"An answer has type '{type}', expected '{expectedType}'.");
            }

            parsed.Add(new ProviderAnswer(name, OpenAiDecisionsReranker.ReadNumber(node![expectedType]), Refused: false));
        }

        var usage = json["usage"];
        return new BatchResponse(
            parsed,
            OpenAiDecisionsReranker.ReadInt(usage?["input_tokens"]),
            CachedInputTokens: 0,
            OpenAiDecisionsReranker.ReadInt(usage?["output_tokens"]),
            json["model"]?.GetValue<string>());
    }
}
