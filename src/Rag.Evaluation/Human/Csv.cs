using System.Text;

namespace Rag.Evaluation.Human;

/// <summary>Minimal RFC 4180 CSV: quoted fields may contain commas, quotes (doubled) and line breaks, which passages routinely do.</summary>
public static class Csv
{
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
                    row.Add(field.ToString());
                    field.Clear();
                    hasContent = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    if (hasContent || field.Length > 0)
                    {
                        row.Add(field.ToString());
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
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
