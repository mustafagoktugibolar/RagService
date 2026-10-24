using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Rag.Core.Options;
using Rag.Core.Resilience;
using Rag.Core.Telemetry;

namespace Rag.Core.Reranking;

/// <summary>One question of a provider request: a positional name (never a chunk id) and the passage it is about.</summary>
public sealed record RelevanceQuestion(string Name, string ChunkId, string Passage);

/// <summary>Questions of one request plus the random fence that separates their passages from instructions.</summary>
public sealed record RelevanceBatch(IReadOnlyList<RelevanceQuestion> Questions, string Delimiter);

/// <summary>A provider's answer to one question, already reduced to a number (or a refusal) by the adapter.</summary>
public sealed record ProviderAnswer(string Name, double? Value, bool Refused);

public sealed record BatchResponse(
    IReadOnlyList<ProviderAnswer> Answers,
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    string? ResolvedModel);

/// <summary>Cumulative provider usage, for budgets, cost reporting and experiments. Thread-safe snapshots.</summary>
public sealed record RerankUsage(
    int Requests,
    int FailedRequests,
    int Questions,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    IReadOnlyList<double> RequestLatenciesMs,
    string? ResolvedModel);

public interface IRerankUsageSource
{
    RerankUsage Usage { get; }
}

/// <summary>The provider answered, but not in a way that can safely be turned into scores. Never retried.</summary>
public sealed class InvalidProviderResponseException(string message) : Exception(message);

/// <summary>The provider rejected the request (4xx other than 408/429). Not transient, not retried. Carries no response body.</summary>
public sealed class RerankProviderException(string provider, int statusCode)
    : Exception($"{provider} rejected the rerank request with HTTP {statusCode}.")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Shared machinery of the provider adapters: split candidates into batches, send them with bounded concurrency through the
/// provider's resilience pipeline, validate every answer, and turn valid ones into scores in [0, 1]. Adapters only implement
/// <see cref="SendAsync"/> (their own wire format). A failed batch loses only its candidates; the call fails only if no batch
/// succeeded. Logs and exceptions never contain the query or passage text.
/// </summary>
public abstract class ProviderReranker : IReranker, IRerankUsageSource, IDisposable
{
    private static readonly ActivitySource Activities = RagTelemetry.ActivitySource;

    private readonly RerankingOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate;
    private readonly ResiliencePipeline _pipeline;
    private readonly object _usageLock = new();
    private readonly List<double> _latencies = [];
    private int _requests, _failed, _questions;
    private long _inputTokens, _cachedTokens, _outputTokens;
    private string? _resolvedModel;

    protected ProviderReranker(
        RerankingOptions options, ILogger logger, string providerId, string model, ResiliencePipeline? pipeline = null)
    {
        _options = options;
        _logger = logger;
        ProviderId = providerId;
        Model = model;
        _gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentRequests));
        _pipeline = pipeline ?? RagPipelines.ForRerankProvider(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
    }

    protected string ProviderId { get; }

    protected string Model { get; }

    protected RelevanceMechanism Mechanism => _options.Mechanism;

    /// <summary>Provider, requested model, rubric version and mechanism: any of them changing changes the ranking.</summary>
    public string Name => $"{ProviderId}:{Model}:{RelevanceRubric.Version}:{_options.Mechanism.ToString().ToLowerInvariant()}";

    /// <summary>Highest value an answer may take: the top level for Score, 1 for Predicate.</summary>
    protected double MaxValue => _options.Mechanism == RelevanceMechanism.Score ? RelevanceRubric.MaxLevel : 1.0;

    protected abstract Task<BatchResponse> SendAsync(string query, RelevanceBatch batch, CancellationToken ct);

    public RerankUsage Usage
    {
        get
        {
            lock (_usageLock)
            {
                return new RerankUsage(_requests, _failed, _questions, _inputTokens, _cachedTokens, _outputTokens, _latencies.ToArray(), _resolvedModel);
            }
        }
    }

    public async Task<IReadOnlyList<RerankScore>> RerankAsync(
        string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var batches = Plan(candidates);
        var outcomes = await Task.WhenAll(batches.Select(batch => RunBatchAsync(query, batch, ct)));

        if (outcomes.All(o => o.Failure is not null))
        {
            throw new InvalidOperationException(
                $"All {outcomes.Length} rerank request(s) to {ProviderId} failed.", outcomes[0].Failure);
        }

        return outcomes.SelectMany(o => o.Scores).ToArray();
    }

    /// <summary>Splits candidates into batches of at most QuestionsPerRequest with positional names, truncating long passages.</summary>
    public IReadOnlyList<RelevanceBatch> Plan(IReadOnlyList<RerankCandidate> candidates)
    {
        var size = Math.Max(1, _options.QuestionsPerRequest);
        return candidates
            .Select((c, i) => new RelevanceQuestion($"c{i}", c.ChunkId, Truncate(c.Text, _options.MaxPassageCharacters)))
            .Chunk(size)
            .Select(chunk => new RelevanceBatch(chunk, RelevanceRubric.NewDelimiter()))
            .ToArray();
    }

    private async Task<(IReadOnlyList<RerankScore> Scores, Exception? Failure)> RunBatchAsync(
        string query, RelevanceBatch batch, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var started = Stopwatch.GetTimestamp();
        using var activity = Activities.StartActivity("rag.rerank.batch");
        activity?.SetTag("rag.rerank.provider", ProviderId);
        activity?.SetTag("rag.rerank.model", Model);
        activity?.SetTag("rag.rerank.questions", batch.Questions.Count);
        BatchResponse? response = null;
        try
        {
            response = await _pipeline.ExecuteAsync(async token => await SendAsync(query, batch, token), ct);
            Record(response, batch.Questions.Count, started, failed: false);   // billed even if the answer turns out unusable
            var scores = Validate(batch, response);
            activity?.SetTag("rag.rerank.outcome", "ok");
            return (scores, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (response is null)
            {
                Record(null, batch.Questions.Count, started, failed: true);
            }
            else
            {
                MarkFailed();   // usage was recorded; the response failed validation
            }

            activity?.SetTag("rag.rerank.outcome", ex.GetType().Name);
            activity?.SetStatus(ActivityStatusCode.Error);
            _logger.LogWarning("Rerank request to {Provider} failed ({ErrorType}, {Questions} questions)",
                ProviderId, ex.GetType().Name, batch.Questions.Count);
            return ([], ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Turns provider answers into scores or refuses to. Duplicate or unknown names make the response untrustworthy (nothing is
    /// applied); refusals, missing answers and non-finite or out-of-range values only drop that one candidate.
    /// </summary>
    internal IReadOnlyList<RerankScore> Validate(RelevanceBatch batch, BatchResponse response)
    {
        var expected = batch.Questions.ToDictionary(q => q.Name, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scores = new List<RerankScore>(batch.Questions.Count);
        var dropped = 0;

        foreach (var answer in response.Answers)
        {
            if (!expected.TryGetValue(answer.Name, out var question))
            {
                throw new InvalidProviderResponseException("The response answered a question that was not asked.");
            }

            if (!seen.Add(answer.Name))
            {
                throw new InvalidProviderResponseException("The response answered the same question more than once.");
            }

            if (answer.Refused || answer.Value is not { } value || !double.IsFinite(value) || value < -1e-6 || value > MaxValue + 1e-6)
            {
                dropped++;
                continue;
            }

            scores.Add(new RerankScore(question.ChunkId, Math.Clamp(value, 0, MaxValue) / MaxValue));
        }

        dropped += expected.Count - seen.Count;   // asked but never answered
        if (dropped > 0)
        {
            _logger.LogWarning("{Dropped} of {Total} rerank answers from {Provider} were refused, missing or invalid and were not scored",
                dropped, expected.Count, ProviderId);
        }

        return scores;
    }

    private void MarkFailed()
    {
        RagTelemetry.RerankRequests.Add(1, new TagList { { "provider", ProviderId }, { "outcome", "invalid" } });
        lock (_usageLock)
        {
            _failed++;
        }
    }

    private void Record(BatchResponse? response, int questions, long started, bool failed)
    {
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        RagTelemetry.RerankDurationMs.Record(elapsed, new TagList { { "provider", ProviderId } });
        RagTelemetry.RerankRequests.Add(1, new TagList { { "provider", ProviderId }, { "outcome", failed ? "failed" : "ok" } });
        if (response is not null)
        {
            RagTelemetry.RerankInputTokens.Add(response.InputTokens, new TagList { { "provider", ProviderId } });
        }

        lock (_usageLock)
        {
            _requests++;
            _questions += questions;
            _latencies.Add(elapsed);
            if (failed)
            {
                _failed++;
            }

            if (response is not null)
            {
                _inputTokens += response.InputTokens;
                _cachedTokens += response.CachedInputTokens;
                _outputTokens += response.OutputTokens;
                if (response.ResolvedModel is not null && response.ResolvedModel != _resolvedModel)
                {
                    if (_resolvedModel is not null || response.ResolvedModel != Model)
                    {
                        _logger.LogInformation("{Provider} resolved model '{Requested}' to '{Resolved}'", ProviderId, Model, response.ResolvedModel);
                    }

                    _resolvedModel = response.ResolvedModel;
                }
            }
        }
    }

    private static string Truncate(string text, int maxCharacters) =>
        text.Length <= maxCharacters ? text : text[..maxCharacters];

    public void Dispose() => _gate.Dispose();
}
