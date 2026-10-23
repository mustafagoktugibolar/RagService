using System.Globalization;
using System.Text;

namespace Rag.Reranking.Onnx;

/// <summary>
/// BERT uncased tokenisation: text cleaning, CJK spacing, accent stripping, lowercasing, whitespace/punctuation splitting and
/// greedy longest-match WordPiece. Written here because Microsoft.ML.Tokenizers 2.0.0's BertTokenizer diverges from the
/// reference on inputs that real documents contain (it drops symbols such as '=', '`', '+', '$', glues '_' into words and, by
/// default, maps accented letters to [UNK]). Equality with the Hugging Face <c>tokenizers</c> output is pinned by fixture tests.
/// </summary>
public sealed class BertWordPieceTokenizer
{
    private const int MaxCharactersPerWord = 100;

    private readonly Dictionary<string, int> _vocabulary;

    public BertWordPieceTokenizer(string vocabPath)
    {
        _vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        var id = 0;
        foreach (var line in File.ReadLines(vocabPath))
        {
            _vocabulary.TryAdd(line.TrimEnd('\r', '\n'), id++);
        }

        UnknownId = Require("[UNK]");
        ClsId = Require("[CLS]");
        SepId = Require("[SEP]");
    }

    public int UnknownId { get; }

    public int ClsId { get; }

    public int SepId { get; }

    /// <summary>Token ids of the text without special tokens.</summary>
    public List<int> Encode(string text)
    {
        var ids = new List<int>();
        foreach (var word in PreTokenize(Normalize(text)))
        {
            WordPiece(word, ids);
        }

        return ids;
    }

    private int Require(string token) =>
        _vocabulary.TryGetValue(token, out var id)
            ? id
            : throw new InvalidDataException($"Vocabulary has no '{token}' token; is this a BERT vocab.txt?");

    private static string Normalize(string text)
    {
        // 1. clean: drop NUL, U+FFFD and other control/format/private characters; any whitespace becomes a space;
        // 2. surround CJK ideographs with spaces so each is its own word.
        var cleaned = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == 0 || rune.Value == 0xFFFD || IsRemoved(rune))
            {
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                cleaned.Append(' ');
            }
            else if (IsCjk(rune.Value))
            {
                cleaned.Append(' ').Append(rune.ToString()).Append(' ');
            }
            else
            {
                cleaned.Append(rune.ToString());
            }
        }

        // 3. strip accents (decompose, drop non-spacing marks), then 4. lowercase — in this order, as the reference does.
        var stripped = new StringBuilder(cleaned.Length);
        foreach (var character in cleaned.ToString().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                stripped.Append(character);
            }
        }

        return stripped.ToString().ToLowerInvariant();
    }

    private static IEnumerable<string> PreTokenize(string text)
    {
        var word = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == ' ')
            {
                if (word.Length > 0)
                {
                    yield return Flush(word);
                }
            }
            else if (IsPunctuation(rune))
            {
                if (word.Length > 0)
                {
                    yield return Flush(word);
                }

                yield return rune.ToString();
            }
            else
            {
                word.Append(rune.ToString());
            }
        }

        if (word.Length > 0)
        {
            yield return Flush(word);
        }

        static string Flush(StringBuilder builder)
        {
            var value = builder.ToString();
            builder.Clear();
            return value;
        }
    }

    private void WordPiece(string word, List<int> output)
    {
        var runes = word.EnumerateRunes().Select(r => r.ToString()).ToArray();
        if (runes.Length > MaxCharactersPerWord)
        {
            output.Add(UnknownId);
            return;
        }

        var pieces = new List<int>();
        var start = 0;
        while (start < runes.Length)
        {
            var end = runes.Length;
            int? match = null;
            while (start < end)
            {
                var candidate = string.Concat(runes[start..end]);
                if (start > 0)
                {
                    candidate = "##" + candidate;
                }

                if (_vocabulary.TryGetValue(candidate, out var id))
                {
                    match = id;
                    break;
                }

                end--;
            }

            if (match is null)
            {
                output.Add(UnknownId);   // one unknown piece makes the whole word unknown
                return;
            }

            pieces.Add(match.Value);
            start = end;
        }

        output.AddRange(pieces);
    }

    private static bool IsRemoved(Rune rune)
    {
        if (rune.Value is '\t' or '\n' or '\r')
        {
            return false;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;
    }

    // BERT treats every ASCII punctuation or symbol character as punctuation, plus all Unicode punctuation categories.
    private static bool IsPunctuation(Rune rune)
    {
        var value = rune.Value;
        if (value is >= 33 and <= 47 or >= 58 and <= 64 or >= 91 and <= 96 or >= 123 and <= 126)
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
    }

    private static bool IsCjk(int cp) =>
        cp is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x2A6DF or >= 0x2A700 and <= 0x2B73F
            or >= 0x2B740 and <= 0x2B81F or >= 0x2B820 and <= 0x2CEAF or >= 0xF900 and <= 0xFAFF or >= 0x2F800 and <= 0x2FA1F;
}
