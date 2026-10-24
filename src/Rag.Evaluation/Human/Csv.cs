using System.Text;

namespace Rag.Evaluation.Human;

/// <summary>
/// Minimal RFC 4180 CSV: quoted fields may contain commas, quotes (doubled) and line breaks, which passages routinely do.
/// <para>
/// <b>Formula-injection guard.</b> These files are opened in spreadsheets by people. A cell that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>,
/// a tab or a carriage return can be run as a formula, so <see cref="Write"/> prefixes such fields with a single quote and <see cref="Read"/> removes
/// that prefix again. The mapping is reversible: a value that itself begins with a quote followed by one of those characters gets a second quote
/// on the way out, so round trips are exact. In a spreadsheet the quote is visible: a leading <c>'</c> on a passage is this safety marker.
/// </para>
/// </summary>
public static class Csv
{
    private static readonly char[] FormulaStarts = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>True for text that begins with a formula character, or with a quote followed by one (the already-prefixed form).</summary>
    public static bool NeedsGuard(string value) =>
        value.Length > 0 && (FormulaStarts.Contains(value[0]) || (value[0] == '\'' && value.Length > 1 && FormulaStarts.Contains(value[1])));

    public static bool StartsLikeFormula(string value) => value.Length > 0 && FormulaStarts.Contains(value[0]);

    public static string Write(IEnumerable<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.Append(string.Join(',', row.Select(Escape))).Append("\r\n");
        }

        return builder.ToString();
    }

    public static List<List<string>> Read(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var hasContent = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    hasContent = true;
                    break;
                case ',':
                    row.Add(Unguard(field.ToString()));
                    field.Clear();
                    hasContent = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    if (hasContent || field.Length > 0)
                    {
                        row.Add(Unguard(field.ToString()));
                        rows.Add(row);
                    }

                    row = [];
                    field.Clear();
                    hasContent = false;
                    break;
                default:
                    field.Append(c);
                    hasContent = true;
                    break;
            }
        }

        if (hasContent || field.Length > 0)
        {
            row.Add(Unguard(field.ToString()));
            rows.Add(row);
        }

        return rows;
    }

    private static string Escape(string value)
    {
        if (NeedsGuard(value))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static string Unguard(string field) => field.Length > 1 && field[0] == '\'' && NeedsGuard(field[1..]) ? field[1..] : field;
}
