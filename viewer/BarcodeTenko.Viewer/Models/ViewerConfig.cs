using System.IO;

namespace BarcodeTenko.Viewer.Models;

public sealed class ViewerConfig
{
    public string ServerUrl { get; set; } = "";
    public string AdminPassword { get; set; } = "";
    public string BinDirectory { get; set; } = Path.Combine("kunugidasaitenko", "bin");
}
