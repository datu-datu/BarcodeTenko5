namespace BarcodeTenko.Viewer.Models;

/// <summary>computeSummary() の集計レスポンス (server/src/summary.js と対応)</summary>
public sealed class SummaryResponse
{
    public string? ServerTime { get; set; }
    public SummarySession? Session { get; set; }
    public int Total { get; set; }
    public int Target { get; set; }
    public double? Rate { get; set; }
    public List<LocationCount> ByLocation { get; set; } = new();
    public int UnassignedLocation { get; set; }
    public int PreSessionTotal { get; set; }
}

public sealed class SummarySession
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public int TargetCount { get; set; }
    public string? StartedAt { get; set; }
    public string? EndedAt { get; set; }
}

public sealed class LocationCount
{
    public int LocationId { get; set; }
    public string Name { get; set; } = "";
    public int Count { get; set; }
}
