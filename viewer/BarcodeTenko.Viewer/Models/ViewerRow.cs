using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BarcodeTenko.Viewer.Models;

public sealed class ViewerRow : INotifyPropertyChanged
{
    private int _studentNumber;
    private string _timeText = "";
    private string _locationName = "";
    private string _className = "";
    private string _name = "";
    private bool _isRecentlyAdded;

    public long? AttendanceId { get; set; }

    public int StudentNumber
    {
        get => _studentNumber;
        set
        {
            if (_studentNumber == value) return;
            _studentNumber = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StudentNumberText));
        }
    }

    public string StudentNumberText => $"{StudentNumber:D5}";

    public string TimeText
    {
        get => _timeText;
        set => SetField(ref _timeText, value);
    }

    public string LocationName
    {
        get => _locationName;
        set => SetField(ref _locationName, value);
    }

    public string ClassName
    {
        get => _className;
        set => SetField(ref _className, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public bool IsRecentlyAdded
    {
        get => _isRecentlyAdded;
        set => SetField(ref _isRecentlyAdded, value);
    }

    public DateTime HighlightUntil { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void CopyFrom(ViewerRow other)
    {
        StudentNumber = other.StudentNumber;
        TimeText = other.TimeText;
        LocationName = other.LocationName;
        ClassName = other.ClassName;
        Name = other.Name;
        IsRecentlyAdded = other.IsRecentlyAdded;
        HighlightUntil = other.HighlightUntil;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
