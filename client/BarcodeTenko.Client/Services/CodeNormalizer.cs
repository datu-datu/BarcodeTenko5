namespace BarcodeTenko.Client.Services;

public static class CodeNormalizer
{
    public const int MaxStudentNumber = 65535;

    /// <summary>
    /// 入力コードから学籍番号(後方5桁)を抽出する。
    /// 厳密に5桁(手入力)または10桁(バーコード)の数字のみを受け付け、後方5桁を採用する。
    /// それ以外の桁数や数字以外の文字が含まれる場合は null を返す。
    /// </summary>
    public static int? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim();

        // 全角数字が含まれていれば半角数字に変換
        var normalized = string.Concat(trimmed.Select(c => c >= '０' && c <= '９' ? (char)(c - '０' + '0') : c));

        // 半角数字のみで構成されているか厳密にチェック
        if (!normalized.All(c => c >= '0' && c <= '9'))
        {
            return null;
        }

        // 5桁または10桁以外はすべて弾く
        if (normalized.Length != 5 && normalized.Length != 10)
        {
            return null;
        }

        var last5 = normalized[^5..];
        if (!int.TryParse(last5, out var value))
        {
            return null;
        }

        if (value < 0 || value > MaxStudentNumber)
        {
            return null;
        }

        return value;
    }
}
