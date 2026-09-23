using System.Globalization;
using System.IO;

namespace BarcodeTenko.Viewer.Services;

/// <summary>
/// client が出力する bin ファイルの読み込み。
/// 形式は README.md「bin ファイル形式」の通り、ヘッダや件数なしの
/// UInt16(リトルエンディアン)連結のみ。1 レコード = 2 バイト、値は学籍番号。
/// </summary>
public static class BinReader
{
    private const string Prefix = "tenko_";

    public static List<int> ReadNumbers(string path)
    {
        var numbers = new List<int>();
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        // 端数が 1 バイト残る場合は無視する
        while (stream.Length - stream.Position >= 2)
        {
            numbers.Add(reader.ReadUInt16());
        }

        return numbers;
    }

    /// <summary>
    /// ファイル名 tenko_&lt;点呼場所&gt;_yyyyMMdd_HHmmss.bin から点呼場所と実施日時を取り出す。
    /// (BinWriter.FinalizeLive の命名規則に合わせる)
    /// タイムスタンプ自体がアンダースコアを含むため、末尾の _&lt;stamp&gt; を切り出して判定する。
    /// </summary>
    public static (string? Location, DateTime? Time) ParseFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var rest = name[Prefix.Length..];
        const string stampFormat = "yyyyMMdd_HHmmss";
        var separator = rest.Length - stampFormat.Length - 1;

        if (separator > 0
            && rest[separator] == '_'
            && DateTime.TryParseExact(rest[(separator + 1)..], stampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return (rest[..separator], time);
        }

        return (string.IsNullOrEmpty(rest) ? null : rest, null);
    }
}
