using System.Diagnostics;
using System.IO;
using System.Text;

namespace BarcodeTenko.Client.Services;

public static class BinWriter
{
    private const string LiveFileName = "tenko_live.bin";

    /// <summary>作業中 bin ファイルのパス (bin/ ディレクトリ内)</summary>
    public static string LiveFilePath(string outputDirectory)
        => Path.Combine(outputDirectory, LiveFileName);

    /// <summary>
    /// 未確定の点呼データを作業中 bin (bin/tenko_live.bin) に全件書き直す。
    /// 取消・全削除・起動時・点呼完了の直前に使う。
    /// </summary>
    public static void WriteLive(IReadOnlyList<int> studentNumbers, string outputDirectory)
    {
        var path = LiveFilePath(outputDirectory);
        if (studentNumbers.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return;
        }

        Directory.CreateDirectory(outputDirectory);
        WriteNumbers(studentNumbers, path);
    }

    /// <summary>
    /// スキャン1件を作業中 bin の末尾に追記する (全件書き直しを避ける高速パス)。
    /// 新規スキャンは未確定(completed = 0)の中で常に最大 id のため、末尾追記で id 順が保たれる。
    /// </summary>
    public static void AppendLive(int studentNumber, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = LiveFilePath(outputDirectory);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)studentNumber);
    }

    /// <summary>
    /// 点呼完了: 作業中 bin (bin/tenko_live.bin) をタイムスタンプ付きの最終ファイル名にリネームする。
    /// </summary>
    public static string FinalizeLive(string outputDirectory, string locationName)
    {
        var livePath = LiveFilePath(outputDirectory);
        if (!File.Exists(livePath))
        {
            throw new FileNotFoundException("出力対象の bin ファイルがありません。");
        }

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
