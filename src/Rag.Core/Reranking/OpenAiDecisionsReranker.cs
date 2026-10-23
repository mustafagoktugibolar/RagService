using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Core.Options;

namespace Rag.Core.Reranking;

/// <summary>
/// OpenAI Decisions adapter (<c>POST /v1/decisions</c>). The query is the shared <c>input</c>; each candidate is one
/// <c>score</c> (or <c>predicate</c>) question named <c>c0…cN</c> whose instructions carry that passage. Authenticated with the
/// existing <c>OpenAI:ApiKey</c>. OpenAI's own DTO shapes live only in this class.
/// </summary>
public sealed class OpenAiDecisionsReranker : ProviderReranker
{
    public const string HttpClientName = "rag-rerank-openai-decisions";
    public const string Provider = "openai-decisions";

    private readonly IHttpClientFactory _http;
    private readonly OpenAiOptions _openAi;
    private readonly OpenAiDecisionsOptions _decisions;

    public OpenAiDecisionsReranker(
        IHttpClientFactory http,
        IOptions<OpenAiOptions> openAi,
        IOptions<OpenAiDecisionsOptions> decisions,
        IOptions<RetrievalOptions> retrieval,
        ILogger<OpenAiDecisionsReranker> logger,
        Resilience.ResiliencePipeline? pipeline = null)
        : base(retrieval.Value.Reranking, logger, Provider, decisions.Value.Model, pipeline)
    {
        _http = http;
        _openAi = openAi.Value;
        _decisions = decisions.Value;
    }

    protected override async Task<BatchResponse> SendAsync(string query, RelevanceBatch batch, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _decisions.Model,
            ["input"] = ProviderHttp.QueryInput(query),
            ["questions"] = new JsonArray(batch.Questions.Select(BuildQuestion).ToArray())
        };

        var extraHeaders = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(_openAi.Organization))
        {
            extraHeaders.Add(new("OpenAI-Organization", _openAi.Organization));
        }

        if (!string.IsNullOrWhiteSpace(_openAi.Project))
        {
            extraHeaders.Add(new("OpenAI-Project", _openAi.Project));
        }

        var json = await ProviderHttp.PostJsonAsync(
            _http.CreateClient(HttpClientName), ProviderHttp.BuildUrl(_decisions.BaseUrl, "/v1/decisions"),
            _openAi.ApiKey, body, Provider, extraHeaders, ct);

        return Parse(json);
    }

    private JsonNode BuildQuestion(RelevanceQuestion question)
    {
        if (Mechanism == RelevanceMechanism.Predicate)
        {
            return new JsonObject
            {
                ["type"] = "predicate",
                ["name"] = question.Name,
                ["instructions"] = RelevanceRubric.PredicateInstructions(question.Passage)
            };
        }

        return new JsonObject
        {
            ["type"] = "score",
            ["name"] = question.Name,
            ["instructions"] = RelevanceRubric.ScoreInstructions(question.Passage),
            ["levels"] = new JsonArray(RelevanceRubric.Levels
                .Select(l => (JsonNode)new JsonObject { ["label"] = l.Label, ["description"] = l.Description }).ToArray())
        };
    }

    internal BatchResponse Parse(JsonObject json)
    {
        if (json["answers"] is not JsonArray answers)
        {
            throw new InvalidProviderResponseException("The response has no 'answers' array.");
        }

        var expectedType = Mechanism == RelevanceMechanism.Score ? "score" : "predicate";
        var valueField = Mechanism == RelevanceMechanism.Score ? "score" : "probability";
        var parsed = new List<ProviderAnswer>(answers.Count);
        foreach (var node in answers)
        {
            var name = node?["name"]?.GetValue<string>()
                ?? throw new InvalidProviderResponseException("An answer has no 'name'.");
            var type = node["type"]?.GetValue<string>();

            if (type == "refusal")
            {
                parsed.Add(new ProviderAnswer(name, null, Refused: true));
            }
            else if (type == expectedType)
            {
                parsed.Add(new ProviderAnswer(name, ReadNumber(node[valueField]), Refused: false));
            }
            else
            {
                throw new InvalidProviderResponseException($"An answer has type '{type}', expected '{expectedType}'.");
            }
        }

        var usage = json["usage"];
        return new BatchResponse(
            parsed,
            ReadInt(usage?["input_tokens"]),
            ReadInt(usage?["input_tokens_details"]?["cached_tokens"]),
            ReadInt(usage?["output_tokens"]),
            ResolvedModel: null);
    }

    internal static double? ReadNumber(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    internal static int ReadInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
}
