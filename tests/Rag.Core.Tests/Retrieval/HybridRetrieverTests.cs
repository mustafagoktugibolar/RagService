using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.Core.Abstractions;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Retrieval;

namespace Rag.Core.Tests.Retrieval;

public sealed class HybridRetrieverTests
{
    private static readonly float[] Embedding = [1f, 0f];

    private static VectorSearchResult R(string chunk, double score = 0) =>
        new() { ChunkId = chunk, DocumentId = "d", Text = chunk, Score = score };

    private static RetrievalRequest Request(Dictionary<string, string>? filters = null, int topK = 3) =>
        new("c", "the query", Embedding, topK, filters);

    private static (HybridRetriever Retriever, IVectorStore Dense, ILexicalSearchStore Lexical) Build(
        IReadOnlyList<VectorSearchResult>? dense = null,
        IReadOnlyList<VectorSearchResult>? lexical = null,
        RetrievalOptions? options = null)
    {
        var store = Substitute.For<IVectorStore, ILexicalSearchStore>();
        var lex = (ILexicalSearchStore)store;
        store.SearchAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(dense ?? []));
        lex.LexicalSearchAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(lexical ?? []));
        var retriever = new HybridRetriever(store, lex, new ReciprocalRankFusion(60),
            options ?? new RetrievalOptions { CandidateK = 20 }, NullLogger<HybridRetriever>.Instance);
        return (retriever, store, lex);
    }

    [Fact]
    public async Task FusesBothLegs_AndTruncatesToTopK()
    {
        var (retriever, _, _) = Build(dense: [R("a"), R("b"), R("c")], lexical: [R("b"), R("c"), R("d")]);

        var outcome = await retriever.RetrieveAsync(Request(topK: 2), CancellationToken.None);

        Assert.Equal(["b", "c"], outcome.Results.Select(r => r.Result.ChunkId));
        Assert.Empty(outcome.Warnings);
        Assert.Equal(["dense", "lexical"], outcome.Results[0].Sources.Select(s => s.Source));
    }

    [Fact]
    public async Task BothLegsReceiveTheSameFilters_AndTheCandidateDepth()
    {
        var (retriever, dense, lexical) = Build(options: new RetrievalOptions { CandidateK = 25 });
        var filters = new Dictionary<string, string> { ["lang"] = "tr" };

        await retriever.RetrieveAsync(Request(filters, topK: 3), CancellationToken.None);

        await dense.Received(1).SearchAsync("c", Embedding, 25, filters, Arg.Any<CancellationToken>());
        await lexical.Received(1).LexicalSearchAsync("c", "the query", 25, filters, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CandidateDepthIsNeverSmallerThanTopK()
    {
        var (retriever, dense, _) = Build(options: new RetrievalOptions { CandidateK = 2 });

        await retriever.RetrieveAsync(Request(topK: 7), CancellationToken.None);

        await dense.Received(1).SearchAsync("c", Embedding, 7, Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LexicalLegFailure_DegradesToDenseResults_AndSaysSo()
    {
        var (retriever, _, lexical) = Build(dense: [R("a", 0.9), R("b", 0.8)]);
        lexical.LexicalSearchAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromException<IReadOnlyList<VectorSearchResult>>(new HttpRequestException("lexical down")));

        var outcome = await retriever.RetrieveAsync(Request(), CancellationToken.None);

        Assert.Equal(["a", "b"], outcome.Results.Select(r => r.Result.ChunkId));
        Assert.Equal([HybridRetriever.LexicalFailed], outcome.Warnings);
    }

    [Fact]
    public async Task DenseLegFailure_DegradesToLexicalResults_AndSaysSo()
    {
        var (retriever, dense, _) = Build(lexical: [R("x")]);
        dense.SearchAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromException<IReadOnlyList<VectorSearchResult>>(new IOException("dense down")));

        var outcome = await retriever.RetrieveAsync(Request(), CancellationToken.None);

        Assert.Equal(["x"], outcome.Results.Select(r => r.Result.ChunkId));
        Assert.Equal([HybridRetriever.DenseFailed], outcome.Warnings);
    }

    [Fact]
    public async Task BothLegsFailing_Throws()
    {
        var (retriever, dense, lexical) = Build();
        dense.SearchAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromException<IReadOnlyList<VectorSearchResult>>(new IOException("d")));
        lexical.LexicalSearchAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromException<IReadOnlyList<VectorSearchResult>>(new IOException("l")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => retriever.RetrieveAsync(Request(), CancellationToken.None));

        Assert.IsType<AggregateException>(ex.InnerException);
    }

    [Fact]
    public async Task Cancellation_IsPropagated_NotTreatedAsADegradation()
    {
        using var cts = new CancellationTokenSource();
        var (retriever, dense, lexical) = Build();
        dense.SearchAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromCanceled<IReadOnlyList<VectorSearchResult>>(new CancellationToken(true)));
        lexical.LexicalSearchAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromCanceled<IReadOnlyList<VectorSearchResult>>(new CancellationToken(true)));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retriever.RetrieveAsync(Request(), cts.Token));
    }

    [Fact]
    public async Task MissingEmbedding_IsAnArgumentError()
    {
        var (retriever, _, _) = Build();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            retriever.RetrieveAsync(new RetrievalRequest("c", "q", null, 3, null), CancellationToken.None));
    }

    [Fact]
    public async Task DenseRetriever_KeepsTheStoresScores_AndRecordsRanks()
    {
        var store = Substitute.For<IVectorStore>();
        store.SearchAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<VectorSearchResult>>([R("a", 0.9), R("a", 0.9), R("b", 0.4)]));

        var outcome = await new DenseRetriever(store).RetrieveAsync(Request(), CancellationToken.None);

        Assert.Equal(["a", "b"], outcome.Results.Select(r => r.Result.ChunkId));   // duplicate chunk collapsed
        Assert.Equal(0.9, outcome.Results[0].Score);
        Assert.Equal(new SourceRank("dense", 2, 0.4), outcome.Results[1].Sources.Single());
    }
}
