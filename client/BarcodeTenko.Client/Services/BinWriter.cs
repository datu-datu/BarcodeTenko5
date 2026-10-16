using System.Diagnostics;
using System.IO;
using System.Text;

namespace BarcodeTenko.Client.Services;

public static class BinWriter
{
    private const string LiveFileName = "tenko_live.bin";
    private const string TempFileName = "tenko_live.bin.tmp";

    /// <summary>作業中 bin ファイルのパス (bin/ ディレクトリ内)</summary>
    public static string LiveFilePath(string outputDirectory)
        => Path.Combine(outputDirectory, LiveFileName);

    /// <summary>
    /// 未確定の点呼データを作業中 bin (bin/tenko_live.bin) にアトミックに全件書き直す。
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
        var tempPath = Path.Combine(outputDirectory, TempFileName);

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            foreach (var number in studentNumbers)
            {
                writer.Write(checked((ushort)number));
            }
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        // 一時ファイルから本番ファイルへアトミックに置換する
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// スキャン1件を作業中 bin の末尾に追記する (全件書き直しを避ける高速パス)。
    /// 追記前のファイルサイズが期待件数 ((expectedTotalCount - 1) * 2) と異なる場合は false を返し、
    /// 呼び出し側で WriteLive による全件再構築へフォールバックさせる。
    /// </summary>
    public static bool TryAppendLive(int studentNumber, int expectedTotalCount, string outputDirectory)
    {
        if (expectedTotalCount <= 0)
        {
            return false;
        }

        Directory.CreateDirectory(outputDirectory);
        var path = LiveFilePath(outputDirectory);
        var expectedPrevBytes = (long)(expectedTotalCount - 1) * sizeof(ushort);

        if (expectedPrevBytes == 0)
        {
            if (File.Exists(path) && new FileInfo(path).Length != 0)
            {
                return false;
            }
        }
        else if (!File.Exists(path) || new FileInfo(path).Length != expectedPrevBytes)
        {
            return false;
        }

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream);
        writer.Write(checked((ushort)studentNumber));
        writer.Flush();
        stream.Flush(flushToDisk: true);
        return true;
    }

    /// <summary>
    /// 点呼完了: 最新の学籍番号リストで確実に書き出してからタイムスタンプ付きの最終ファイル名にリネームする。
    /// </summary>
    public static string FinalizeLive(IReadOnlyList<int> studentNumbers, string outputDirectory, string locationName)
    {
        if (studentNumbers.Count == 0)
        {
            throw new InvalidOperationException("出力対象の点呼データがありません。");
        }

        // 例外を握りつぶさず、確実に最新データで tenko_live.bin を書き直す
        WriteLive(studentNumbers, outputDirectory);

        var livePath = LiveFilePath(outputDirectory);
        var baseName = $"tenko_{Sanitize(locationName)}_{DateTime.Now:yyyyMMdd_HHmmss}";
        var finalPath = Path.Combine(outputDirectory, $"{baseName}.bin");
        var suffix = 1;
        while (File.Exists(finalPath))
        {
            finalPath = Path.Combine(outputDirectory, $"{baseName}_{suffix++}.bin");
        }

        File.Move(livePath, finalPath);
        return finalPath;
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
