using System.Text;

namespace BarcodeTenko.Viewer.Services;

public static class SimpleCsv
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closedQuote = false;
        var line = 1;

        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];

            if (quoted)
            {
                if (c == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                        closedQuote = true;
                    }
                }
                else
                {
                    field.Append(c);
                    if (c == '\n') line++;
                }

                continue;
            }

            if (closedQuote && c != ',' && c != '\r' && c != '\n' && c != ' ' && c != '\t')
            {
                throw new FormatException($"CSVの引用符の後に不正な文字があります ({line}行目)。");
            }

            if (c == '"')
            {
                if (field.Length > 0 || closedQuote)
                {
                    throw new FormatException($"CSVの引用符の位置が不正です ({line}行目)。");
                }

                quoted = true;
                continue;
            }

            if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                closedQuote = false;
                continue;
            }

            if (c == '\r' || c == '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                rows.Add(fields);
                fields = new List<string>();
                closedQuote = false;
                if (c == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                line++;
                continue;
            }

            if (!closedQuote) field.Append(c);
        }

        if (quoted)
        {
            throw new FormatException($"CSVの引用符が閉じられていません ({line}行目)。");
        }

        if (field.Length > 0 || fields.Count > 0 || closedQuote)
        {
            fields.Add(field.ToString());
            rows.Add(fields);
        }

        return rows;
    }
}
