namespace Rag.Reranking.Onnx;

/// <summary>One tokenised (query, document) pair: [CLS] query [SEP] document [SEP], with segment ids 0 then 1.</summary>
public sealed record EncodedPair(int[] InputIds, int[] TokenTypeIds)
{
    public int Length => InputIds.Length;
}

/// <summary>
/// Turns a (query, document) pair into model input, entirely in .NET: <see cref="BertWordPieceTokenizer"/> plus
/// <c>longest_first</c> truncation to the maximum length. A pinned fixture compares the ids with the reference exactly.
/// </summary>
public sealed class CrossEncoderTokenizer
{
    private readonly BertWordPieceTokenizer _tokenizer;
    private readonly int _maxLength;

    public CrossEncoderTokenizer(string vocabPath, int maxLength = 512)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 8);
        _maxLength = maxLength;
        _tokenizer = new BertWordPieceTokenizer(vocabPath);
    }

    public EncodedPair Encode(string query, string document)
    {
        var first = _tokenizer.Encode(query);
        var second = _tokenizer.Encode(document);

        // longest_first: while over budget, drop the last token of the longer sequence ([CLS] + 2x[SEP] take three slots).
        var budget = _maxLength - 3;
        while (first.Count + second.Count > budget)
        {
            if (first.Count > second.Count)
            {
                first.RemoveAt(first.Count - 1);
            }
            else
            {
                second.RemoveAt(second.Count - 1);
            }
        }

        var ids = new int[first.Count + second.Count + 3];
        var types = new int[ids.Length];
        ids[0] = _tokenizer.ClsId;
        first.CopyTo(ids, 1);
        ids[first.Count + 1] = _tokenizer.SepId;
        second.CopyTo(ids, first.Count + 2);
        ids[^1] = _tokenizer.SepId;
        for (var i = first.Count + 2; i < types.Length; i++)
        {
            types[i] = 1;
        }

        return new EncodedPair(ids, types);
    }
}
