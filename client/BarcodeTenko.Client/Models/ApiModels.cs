namespace BarcodeTenko.Client.Models;

public sealed class ScanResponse
{
    public bool Ok { get; set; }
    public bool Created { get; set; }
    public int StudentNumber { get; set; }
    public int? SessionId { get; set; }
    public string? ReceivedAt { get; set; }
}

public sealed class SessionInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public int TargetCount { get; set; }
}

public sealed class LocationCount
{
    public int LocationId { get; set; }
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

public sealed class StatusResponse
{
    public SessionInfo? Session { get; set; }
    public int Total { get; set; }
    public int Target { get; set; }
    public double? Rate { get; set; }
    public List<LocationCount> ByLocation { get; set; } = new();
    public int UnassignedLocation { get; set; }
    public int PreSessionTotal { get; set; }
}
