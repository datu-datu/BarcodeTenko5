using BarcodeTenko.Client.Models;

namespace BarcodeTenko.Client.ViewModels;

public sealed class ScanRow
{
    public ScanRow(ScanRecord record)
    {
        Record = record;
    }

    public ScanRecord Record { get; }

    public int StudentNumber => Record.StudentNumber;

    public string StudentNumberText => $"{Record.StudentNumber:D5}";

    public string LocationName => Record.LocationName;

    public string TimeText => Record.CreatedAt.LocalDateTime.ToString("HH:mm:ss");

    public bool IsRecentlyAdded { get; set; }
}
