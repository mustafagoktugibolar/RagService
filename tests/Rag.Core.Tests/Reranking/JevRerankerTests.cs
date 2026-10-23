using System.Net;
using System.Text.Json.Nodes;
using Rag.Core.Options;
using Rag.Core.Reranking;
using Rag.Core.Resilience;

namespace Rag.Core.Tests.Reranking;

public sealed class JevRerankerTests
{
    private const string SecretKey = "tsk-test-SECRET-jev-456";

    private static readonly RerankCandidate[] Three =
    [
        new("rabbitmq:0", "dead letter exchange routes rejected messages"),
        new("polly:0", "retry with exponential backoff and jitter"),
        new("redis:0", "ttl and eviction policy")
    ];

    private static JevReranker Build(
        ScriptedHandler handler,
        RerankingOptions? options = null,
        CapturingLogger<JevReranker>? logger = null,
        JevOptions? jev = null,
        ResiliencePipeline? pipeline = null) =>
        new(new FakeHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(jev ?? new JevOptions { ApiKey = SecretKey }),
            Microsoft.Extensions.Options.Options.Create(new RetrievalOptions { Reranking = options ?? RerankTestData.Options() }),
            logger ?? new CapturingLogger<JevReranker>(),
            pipeline ?? RerankTestData.FastPipeline());

    private static string Answers(
        (string Name, double Value)[] answers, string type = "score", string model = "jev-1.13.0", int input = 400, int output = 18)
    {
        var map = new JsonObject();
        foreach (var (name, value) in answers)
        {
            map[name] = new JsonObject { ["type"] = type, [type] = value, ["confidence"] = 0.8 };
        }

        return new JsonObject
        {
            ["model"] = model,
            ["answers"] = map,
            ["usage"] = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output }
        }.ToJsonString();
    }

    private static (string, double)[] Cs(params double[] values) => values.Select((v, i) => ($"c{i}", v)).ToArray();

    private static JsonObject BodyOf(ScriptedHandler handler) => JsonNode.Parse(handler.Calls[0].Body)!.AsObject();

    [Fact]
    public async Task Request_TargetsSystemOne_WithBearerAuth_AndAnObjectOfNamedQuestions()
    {
        var handler = new ScriptedHandler().Reply(HttpStatusCode.OK, Answers(Cs(3, 2, 1)));

        await Build(handler).RerankAsync("how do dead letters work", Three, CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", call.Request.RequestUri!.ToString());
        Assert.Equal(SecretKey, call.Request.Headers.Authorization!.Parameter);

        var body = BodyOf(handler);
        Assert.Equal("jev-latest", body["model"]!.GetValue<string>());
        Assert.Equal("Search query: how do dead letters work", body["state"]!.GetValue<string>());
        var questions = body["questions"]!.AsObject();
        Assert.Equal(["c0", "c1", "c2"], questions.Select(q => q.Key));
        var first = questions["c0"]!;
        Assert.Equal("score", first["type"]!.GetValue<string>());
        Assert.Equal(RelevanceRubric.Levels.Select(l => l.Description), first["criteria"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Contains("dead letter exchange routes rejected messages", first["instructions"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChunkIdsNeverLeaveTheProcess()
    {
        var handler = new ScriptedHandler().Reply(HttpStatusCode.OK, Answers(Cs(3, 2, 1)));

        await Build(handler).RerankAsync("q", Three, CancellationToken.None);

        Assert.DoesNotContain("rabbitmq:0", handler.Calls[0].Body);
    }

    [Fact]
    public async Task ScoresAreNormalisedToZeroOne_AndAddressedByChunkId()
    {
        var handler = new ScriptedHandler().Reply(HttpStatusCode.OK, Answers(Cs(2.4, 1.2, 0)));

        var scores = (await Build(handler).RerankAsync("q", Three, CancellationToken.None)).ToDictionary(s => s.ChunkId, s => s.Score);

        Assert.Equal(0.8, scores["rabbitmq:0"], 9);
        Assert.Equal(0.4, scores["polly:0"], 9);
        Assert.Equal(0.0, scores["redis:0"], 9);
    }

    [Fact]
    public async Task Noul_IsUsedForThePredicateMechanism_WithTrueFalseCriteria()
    {
        var handler = new ScriptedHandler().Reply(HttpStatusCode.OK, Answers(Cs(0.97, 0.1, 0.5), type: "noul"));
        var reranker = Build(handler, RerankTestData.Options(mechanism: RelevanceMechanism.Predicate));

        var scores = (await reranker.RerankAsync("q", Three, CancellationToken.None)).ToDictionary(s => s.ChunkId, s => s.Score);

        var question = BodyOf(handler)["questions"]!["c0"]!;
        Assert.Equal("noul", question["type"]!.GetValue<string>());
        Assert.NotNull(question["criteria"]!["true"]);
        Assert.NotNull(question["criteria"]!["false"]);
        Assert.Equal(0.97, scores["rabbitmq:0"], 9);
        Assert.Equal("jev:jev-latest:rel-v1:predicate", reranker.Name);
    }

    [Fact]
    public async Task TheResolvedModelVersion_IsRecorded_SoAnUnpinnedAliasCannotChangeSilently()
    {
        var handler = new ScriptedHandler().Reply(HttpStatusCode.OK, Answers(Cs(3, 2, 1), model: "jev-1.13.0"));
        var reranker = Build(handler);

        await reranker.RerankAsync("q", Three, CancellationToken.None);

        Assert.Equal("jev-1.13.0", reranker.Usage.ResolvedModel);
        Assert.Equal("jev:jev-latest:rel-v1:score", reranker.Name);   // identity still names the requested alias
    }

    [Theory]
    [InlineData("unknown question name")]
    [InlineData("wrong answer type")]
    [InlineData("answers is an array")]
    public async Task UntrustworthyResponses_ApplyNothing(string kind)
    {
        var body = kind switch
        {
            "unknown question name" => Answers([("zzz", 1)]),
            "wrong answer type" => Answers(Cs(1, 1, 1), type: "noul"),
            _ => """{"answers":[],"usage":{"input_tokens":1}}"""
        };
        var handler = new ScriptedHandler().Reply(HttpStatusCode.OK, body);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Build(handler).RerankAsync("q", Three, CancellationToken.None));

        Assert.IsType<InvalidProviderResponseException>(ex.InnerException);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task MissingAndOutOfRangeAnswers_DropOnlyThoseCandidates()
    {
        var handler = new ScriptedHandler().Reply(HttpStatusCode.OK, Answers([("c0", 3), ("c1", 7.5)]));   // c1 out of range, c2 missing

        var scores = await Build(handler).RerankAsync("q", Three, CancellationToken.None);

        var only = Assert.Single(scores);
        Assert.Equal("rabbitmq:0", only.ChunkId);
    }

    [Fact]
    public async Task Jev422_IsNotRetried_AndItsEchoedInputIsNeverLogged()
    {
        var logger = new CapturingLogger<JevReranker>();
        var handler = new ScriptedHandler().Reply(HttpStatusCode.UnprocessableEntity,
            """{"detail":[{"loc":["body","state"],"msg":"Field required","type":"missing","input":"CONFIDENTIAL echoed state"}]}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler, logger: logger).RerankAsync("CONFIDENTIAL query", Three, CancellationToken.None));

        Assert.IsType<RerankProviderException>(ex.InnerException);
        Assert.Single(handler.Calls);
        var everything = ex + string.Join("\n", logger.Entries);
        Assert.DoesNotContain("CONFIDENTIAL", everything);
        Assert.DoesNotContain(SecretKey, everything);
    }

    [Fact]
    public async Task RateLimiting_IsRetried_ThenSucceeds()
    {
        var handler = new ScriptedHandler().Reply(HttpStatusCode.TooManyRequests).Reply(HttpStatusCode.OK, Answers(Cs(3, 2, 1)));

        var scores = await Build(handler).RerankAsync("q", Three, CancellationToken.None);

        Assert.Equal(3, scores.Count);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Usage_RecordsInputAndOutputTokens()
    {
        var handler = new ScriptedHandler().Reply(HttpStatusCode.OK, Answers(Cs(3, 2, 1), input: 410, output: 20));
        var reranker = Build(handler);

        await reranker.RerankAsync("q", Three, CancellationToken.None);

        Assert.Equal(410, reranker.Usage.InputTokens);
        Assert.Equal(20, reranker.Usage.OutputTokens);
    }

    [Fact]
    public async Task Batches_UseTheSameRubricAndKeepGlobalPositionalNames()
    {
        var candidates = Enumerable.Range(0, 6).Select(i => new RerankCandidate($"d{i}:0", $"p{i}")).ToArray();
        var handler = new ScriptedHandler()
            .Reply(HttpStatusCode.OK, Answers([("c0", 2), ("c1", 2), ("c2", 2)]))
            .Reply(HttpStatusCode.OK, Answers([("c3", 2), ("c4", 2), ("c5", 2)]));

        var scores = await Build(handler, RerankTestData.Options(questionsPerRequest: 3, concurrency: 1)).RerankAsync("q", candidates, CancellationToken.None);

        Assert.Equal(6, scores.Count);
        Assert.Equal(["c3", "c4", "c5"], JsonNode.Parse(handler.Calls[1].Body)!["questions"]!.AsObject().Select(q => q.Key));
    }
}
