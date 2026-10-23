using Rag.Core.Retrieval;

namespace Rag.Core.Tests.Retrieval;

public sealed class SparseTextEncoderTests
{
    [Fact]
    public void Tokenizer_LowercasesSplitsOnPunctuationAndDropsSingleCharacters()
    {
        Assert.Equal(
            ["dead", "letter", "exchange", "ef", "construction", "k1", "cat"],
            LexicalTokenizer.Tokenize("x-dead-letter-exchange; EF_construction, K1 a cat"));
    }

    [Fact]
    public void Tokenizer_KeepsUnicodeLettersAndDigits() =>
        Assert.Equal(["çalışan", "işçi", "2024"], LexicalTokenizer.Tokenize("Çalışan, işçi 2024!"));

    [Fact]
    public void IndexForToken_IsStableAcrossRuns()
    {
        // FNV-1a 32-bit of "apple" — pinned so an accidental change of hash breaks existing indexes loudly.
        Assert.Equal(SparseTextEncoder.IndexForToken("apple"), SparseTextEncoder.IndexForToken("apple"));
        Assert.Equal(0x10bc2abfu, SparseTextEncoder.IndexForToken("apple"));
        Assert.NotEqual(SparseTextEncoder.IndexForToken("apple"), SparseTextEncoder.IndexForToken("apples"));
    }

    [Fact]
    public void Document_RepeatedTermsSaturate_AndIndicesAreUniqueAndSorted()
    {
        var vector = SparseTextEncoder.EncodeDocument("cat cat cat cat dog");

        Assert.Equal(2, vector.Indices.Length);
        Assert.Equal(vector.Indices.Order().ToArray(), vector.Indices);
        var cat = vector.Values[Array.IndexOf(vector.Indices, SparseTextEncoder.IndexForToken("cat"))];
        var dog = vector.Values[Array.IndexOf(vector.Indices, SparseTextEncoder.IndexForToken("dog"))];
        Assert.True(cat > dog);
        Assert.True(cat < 4 * dog, "four occurrences must weigh far less than four times one occurrence");
    }

    [Fact]
    public void Document_TermWeightFollowsTheBm25TermFrequencyComponent()
    {
        // tf = 1, dl = 1 token: 1 * (k1 + 1) / (1 + k1 * (1 - b + b * dl / avgdl))
        var vector = SparseTextEncoder.EncodeDocument("solo");
        var expected = 2.2 / (1 + 1.2 * (1 - 0.75 + 0.75 * 1.0 / SparseTextEncoder.AssumedAverageLength));

        Assert.Equal(expected, vector.Values[0], 5);
    }

    [Fact]
    public void Query_UsesWeightOnePerDistinctTerm_SoRepeatsDoNotInflateTheScore()
    {
        var vector = SparseTextEncoder.EncodeQuery("cat cat dog");

        Assert.Equal(2, vector.Indices.Length);
        Assert.All(vector.Values, v => Assert.Equal(1f, v));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b c")]       // only single-character tokens
    public void TextWithoutIndexableTerms_EncodesToAnEmptyVector(string text)
    {
        Assert.True(SparseTextEncoder.EncodeDocument(text).IsEmpty);
        Assert.True(SparseTextEncoder.EncodeQuery(text).IsEmpty);
    }
}
