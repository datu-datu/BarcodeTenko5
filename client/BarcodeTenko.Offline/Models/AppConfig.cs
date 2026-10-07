using System.IO;

namespace BarcodeTenko.Offline.Models;

public sealed class AppConfig
{
    public string DataDirectory { get; set; } = Path.Combine("kunugidasaitenko", "data");
    public string OutputDirectory { get; set; } = Path.Combine("kunugidasaitenko", "bin");
    public bool SoundEnabled { get; set; } = true;
    public List<Location> Locations { get; set; } = new();

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(OutputDirectory);
    }
}
