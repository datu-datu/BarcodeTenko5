using System.Text.Json.Serialization;

namespace BarcodeTenko.Viewer.Models;

/// <summary>GET /admin/api/scans のレスポンス (limit 上限 500 のためページングが必要)</summary>
public sealed class ScansPageResponse
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("rows")]
    public List<AttendanceRow> Rows { get; set; } = new();
}
