namespace BarcodeTenko.Viewer.Models;

/// <summary>名簿 CSV の 1 行: 学籍番号 + クラス + 氏名</summary>
public sealed class RosterEntry
{
    public int StudentNumber { get; set; }
    public string ClassName { get; set; } = "";
    public string Name { get; set; } = "";
}
