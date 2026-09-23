using System.IO;

namespace BarcodeTenko.Client.Models;

public sealed class AppConfig
{
    public string ServerUrl { get; set; } = "";
    public string ClientToken { get; set; } = "";
    public string ClientId { get; set; } = "";
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
