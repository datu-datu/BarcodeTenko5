namespace BarcodeTenko.Viewer.Models;

public sealed class ScanEvent
{
    public long Id { get; set; }
    public string ClientScanId { get; set; } = "";
    public int StudentNumber { get; set; }
    public int? SessionId { get; set; }
    public int? LocationId { get; set; }
    public string? LocationName { get; set; }
    public string? ReceivedAt { get; set; }
}

public sealed class CancelEvent
{
    public long Id { get; set; }
    public string ClientScanId { get; set; } = "";
    public int StudentNumber { get; set; }
    public int? SessionId { get; set; }
    public int? LocationId { get; set; }
}

public sealed class ServerEvent
{
    public string Name { get; init; } = "";
    public string Data { get; init; } = "";
}
