using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Resilience;
using Rag.Core.Retrieval;

namespace Rag.Core.Tests.Reranking;

/// <summary>Scripted HTTP server: each request pops the next reply; every request body is captured.</summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _script = new();
    private int _inFlight;

    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

    public int MaxInFlight { get; private set; }

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public ScriptedHandler Reply(HttpStatusCode status, string body = "{}") =>
        Add(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    /// <summary>Answers exactly the OpenAI questions the request asked, scoring each by its instructions text; arrival order is irrelevant.</summary>
    public ScriptedHandler EchoOpenAi(Func<string, double> scoreOf, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            Add(async request =>
            {
                var body = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                var answers = new System.Text.Json.Nodes.JsonArray(body["questions"]!.AsArray().Select(q =>
                    (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "score",
                        ["name"] = q!["name"]!.GetValue<string>(),
                        ["score"] = scoreOf(q["instructions"]!.GetValue<string>())
                    }).ToArray());
                return Json(new System.Text.Json.Nodes.JsonObject { ["answers"] = answers, ["usage"] = new System.Text.Json.Nodes.JsonObject { ["input_tokens"] = 1 } });
            });
        }

        return this;
    }

    public ScriptedHandler EchoOpenAi(double score, int times = 1) => EchoOpenAi(_ => score, times);

    /// <summary>Same for Jev: answers every named question of the request.</summary>
    public ScriptedHandler EchoJev(Func<string, double> scoreOf, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            Add(async request =>
            {
                var body = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                var answers = new System.Text.Json.Nodes.JsonObject();
                foreach (var (name, question) in body["questions"]!.AsObject())
                {
                    answers[name] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "score",
                        ["score"] = scoreOf(question!["instructions"]!.GetValue<string>())
                    };
                }

                return Json(new System.Text.Json.Nodes.JsonObject
                {
                    ["model"] = "jev-1.13.0",
                    ["answers"] = answers,
                    ["usage"] = new System.Text.Json.Nodes.JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 }
                });
            });
        }

        return this;
    }

    private static HttpResponseMessage Json(System.Text.Json.Nodes.JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    public ScriptedHandler Add(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply)
    {
        _script.Enqueue(reply);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        var now = Interlocked.Increment(ref _inFlight);
        lock (Calls)
        {
            Calls.Add((request, body));
            MaxInFlight = Math.Max(MaxInFlight, now);
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, ct);
            }

            Func<HttpRequestMessage, Task<HttpResponseMessage>> next;
            lock (_script)
            {
                next = _script.Count > 0 ? _script.Dequeue() : throw new InvalidOperationException("Script exhausted: unexpected extra request.");
            }

            return await next(request);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

public sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>Captures every log entry, formatted, so tests can assert nothing sensitive was written.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add($"{formatter(state, exception)} {exception}");
        }
    }
}

public static class RerankTestData
{
    /// <summary>Fast retries so transient-failure tests do not sleep for seconds. Same semantics as the production pipeline.</summary>
    public static ResiliencePipeline FastPipeline(int retries = 2, int timeoutMs = 2000) => new(
        new CircuitBreakerOptions { ShouldHandle = RagPipelinesAccess.IsTransient, MinimumThroughput = 1000 },
        new RetryOptions { MaxRetryAttempts = retries, Delay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5), ShouldHandle = RagPipelinesAccess.IsTransient },
        TimeSpan.FromMilliseconds(timeoutMs));

    public static RerankingOptions Options(
        int questionsPerRequest = 20, int concurrency = 4, RelevanceMechanism mechanism = RelevanceMechanism.Score, int timeoutSeconds = 5) => new()
    {
        Enabled = true,
        QuestionsPerRequest = questionsPerRequest,
        MaxConcurrentRequests = concurrency,
        Mechanism = mechanism,
        TimeoutSeconds = timeoutSeconds
    };

    public static VectorSearchResult Chunk(string id, string text = "", double score = 0, Dictionary<string, string>? metadata = null) =>
        new() { ChunkId = id, DocumentId = id.Split(':')[0], Text = string.IsNullOrEmpty(text) ? $"text of {id}" : text, Score = score, Metadata = metadata };

    public static FusedResult Fused(string id, double score = 0, string source = "dense", Dictionary<string, string>? metadata = null, string text = "") =>
        new(Chunk(id, text, score, metadata), score, [new SourceRank(source, 1, score)]);
}

/// <summary>The production transient-error rule, reproduced for the test pipeline (RagPipelines is internal).</summary>
public static class RagPipelinesAccess
{
    public static bool IsTransient(Exception ex) => ex is HttpRequestException or TimeoutRejectedException or IOException;
}
