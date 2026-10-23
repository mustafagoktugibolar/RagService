using Rag.Reranking.Onnx;

namespace Rag.Reranking.Onnx.Tests;

public sealed class CrossEncoderTokenizerTests
{
    private static CrossEncoderTokenizer Build(int maxLength = 512) => new(TinyVocab.Create(), maxLength);

    [Fact]
    public void Pair_IsLaidOutAsClsQuerySepDocumentSep_WithSegmentIds()
    {
        var pair = Build().Encode("dead letter", "queue retry");

        Assert.Equal(TinyVocab.Cls, pair.InputIds[0]);
        Assert.Equal(TinyVocab.Sep, pair.InputIds[3]);
        Assert.Equal(TinyVocab.Sep, pair.InputIds[^1]);
        Assert.Equal(7, pair.Length);                     // [CLS] 2 [SEP] 2 [SEP]
        Assert.Equal([0, 0, 0, 0, 1, 1, 1], pair.TokenTypeIds);
    }

    [Fact]
    public void Text_IsLowercasedBeforeTokenisation()
    {
        var tokenizer = Build();

        Assert.Equal(tokenizer.Encode("dead letter", "queue").InputIds, tokenizer.Encode("DEAD Letter", "QUEUE").InputIds);
    }

    [Fact]
    public void LongDocument_IsTruncatedToTheMaximum_KeepingTheShortQueryIntact()
    {
        var pair = Build(maxLength: 12).Encode("dead letter", string.Join(' ', Enumerable.Repeat("queue", 30)));

        Assert.Equal(12, pair.Length);
        Assert.Equal(TinyVocab.Sep, pair.InputIds[^1]);
        Assert.Equal(2, pair.TokenTypeIds.Count(t => t == 0) - 2);   // 2 query tokens + [CLS] + first [SEP] = 4 zeros
    }

    [Fact]
    public void WhenBothSidesAreLong_TruncationShavesTheLongerOneFirst()
    {
        var pair = Build(maxLength: 13).Encode(
            string.Join(' ', Enumerable.Repeat("dead", 8)), string.Join(' ', Enumerable.Repeat("queue", 8)));

        var queryTokens = pair.TokenTypeIds.Count(t => t == 0) - 2;
        var documentTokens = pair.TokenTypeIds.Count(t => t == 1) - 1;
        Assert.Equal(13, pair.Length);
        Assert.InRange(Math.Abs(queryTokens - documentTokens), 0, 1);   // balanced, not one side wiped out
    }

    [Fact]
    public void EmptyDocument_StillProducesAValidPair()
    {
        var pair = Build().Encode("dead", string.Empty);

        Assert.Equal([TinyVocab.Cls, pair.InputIds[1], TinyVocab.Sep, TinyVocab.Sep], pair.InputIds);
    }

    [Fact]
    public void TooSmallMaxLength_IsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(maxLength: 4));
}
