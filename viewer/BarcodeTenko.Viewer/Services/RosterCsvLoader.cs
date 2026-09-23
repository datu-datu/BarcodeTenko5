using System.IO;
using System.Text;
using BarcodeTenko.Viewer.Models;

namespace BarcodeTenko.Viewer.Services;

/// <summary>名簿CSV (学籍番号, クラス, 氏名の順) を読み込む。</summary>
public static class RosterCsvLoader
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static List<RosterEntry> Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static List<RosterEntry> Load(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var text = Decode(buffer.ToArray());
        var rows = SimpleCsv.Parse(text);
        var entries = new List<RosterEntry>();

        foreach (var columns in rows)
        {
            if (columns.Count < 3)
            {
                continue;
            }

            var studentNumber = StudentNumber.Normalize(columns[0]);
            if (studentNumber is null)
            {
                continue;
            }

            entries.Add(new RosterEntry
            {
                StudentNumber = studentNumber.Value,
                ClassName = columns[1].Trim(),
                Name = columns[2].Trim()
            });
        }

        return entries;
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(932).GetString(bytes);
        }
    }
}
