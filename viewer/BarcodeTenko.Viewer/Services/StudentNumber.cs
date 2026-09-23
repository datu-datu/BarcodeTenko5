namespace BarcodeTenko.Viewer.Services;

public static class StudentNumber
{
    public const int MaxStudentNumber = 65535;

    /// <summary>
    /// 学籍番号らしき文字列を整数に正規化する。
    /// 全角数字は半角に変換し、数字以外は除去する。10桁(バーコード)相当の長い値は
    /// サーバ側の規則 (server/src/code.js) に合わせて後方5桁を採用する。
    /// 数字が 1 つも無ければ null を返す。
    /// </summary>
    public static int? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var normalized = string.Concat(input.Trim().Select(c => c >= '０' && c <= '９' ? (char)(c - '０' + '0') : c));
        var digits = new string(normalized.Where(c => c >= '0' && c <= '9').ToArray());
        if (digits.Length == 0)
        {
            return null;
        }

        var last5 = digits.Length > 5 ? digits[^5..] : digits;
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
