namespace BarcodeTenko.Offline.Models;

public sealed class ScanRecord
{
    public long Id { get; set; }
    public int StudentNumber { get; set; }
    public int LocationId { get; set; }
    public string LocationName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public bool Completed { get; set; }
}
