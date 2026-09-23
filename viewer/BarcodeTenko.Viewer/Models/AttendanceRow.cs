using System.Text.Json.Serialization;

namespace BarcodeTenko.Viewer.Models;

/// <summary>GET /admin/api/scans の 1 行 (server/src/routes/admin.js の SELECT と対応)</summary>
public sealed class AttendanceRow
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("student_number")]
    public int StudentNumber { get; set; }

    [JsonPropertyName("session_id")]
    public int? SessionId { get; set; }

    [JsonPropertyName("location_id")]
    public int? LocationId { get; set; }

    [JsonPropertyName("received_at")]
    public string? ReceivedAt { get; set; }

    [JsonPropertyName("client_time")]
    public string? ClientTime { get; set; }

    [JsonPropertyName("deleted")]
    public int Deleted { get; set; }

    [JsonPropertyName("deleted_at")]
    public string? DeletedAt { get; set; }

    [JsonPropertyName("location_name")]
    public string? LocationName { get; set; }

    [JsonPropertyName("session_name")]
    public string? SessionName { get; set; }
}
