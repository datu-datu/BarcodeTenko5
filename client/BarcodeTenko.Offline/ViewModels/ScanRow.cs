using System.ComponentModel;
using BarcodeTenko.Offline.Models;

namespace BarcodeTenko.Offline.ViewModels;

public sealed class ScanRow : INotifyPropertyChanged
{
    private bool _isRecentlyAdded;

    public ScanRow(ScanRecord record)
    {
        Record = record;
    }

    public ScanRecord Record { get; }

    public int StudentNumber => Record.StudentNumber;

    public string StudentNumberText => $"{Record.StudentNumber:D5}";

    public string LocationName => Record.LocationName;

    public string TimeText => Record.CreatedAt.LocalDateTime.ToString("HH:mm:ss");

    public bool IsRecentlyAdded
    {
        get => _isRecentlyAdded;
        set
        {
            if (_isRecentlyAdded == value)
            {
                return;
            }

            _isRecentlyAdded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRecentlyAdded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
