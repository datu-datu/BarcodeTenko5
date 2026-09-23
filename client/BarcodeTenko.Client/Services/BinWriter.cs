using System.Diagnostics;
using System.IO;
using System.Text;

namespace BarcodeTenko.Client.Services;

public static class BinWriter
{
    /// <summary>常時出力される作業中 bin ファイルのパス（data/ ディレクトリ内）</summary>
    public static string LiveFilePath(string dataDirectory)
        => Path.Combine(dataDirectory, "tenko_live.bin");

    /// <summary>
    /// 未完了の点呼データを作業中 bin (data/tenko_live.bin) に全件書き直す。
    /// スキャン追加・取り消しのたびに呼び、常に最新状態を出力しておく。
    /// </summary>
    public static void WriteLive(IReadOnlyList<int> studentNumbers, string dataDirectory)
    {
        var path = LiveFilePath(dataDirectory);
        if (studentNumbers.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return;
        }

        Directory.CreateDirectory(dataDirectory);
        WriteNumbers(studentNumbers, path);
    }

    /// <summary>
    /// 点呼完了: 作業中 bin (data/) をタイムスタンプ付きの最終ファイル名にして出力ディレクトリ (bin/) に移動する。
    /// </summary>
    public static string FinalizeLive(string dataDirectory, string outputDirectory, string locationName)
    {
        var livePath = LiveFilePath(dataDirectory);
        if (!File.Exists(livePath))
        {
            throw new FileNotFoundException("出力対象の bin ファイルがありません。");
        }

        Directory.CreateDirectory(outputDirectory);
        var fileName = $"tenko_{Sanitize(locationName)}_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
        var finalPath = Path.Combine(outputDirectory, fileName);
        File.Move(livePath, finalPath);
        return finalPath;
    }

    private static void WriteNumbers(IReadOnlyList<int> studentNumbers, string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        foreach (var number in studentNumbers)
        {
            writer.Write((ushort)number);
        }
    }

    public static void RevealInExplorer(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        var result = builder.ToString().Trim();
        return string.IsNullOrEmpty(result) ? "tenko" : result;
    }
}
