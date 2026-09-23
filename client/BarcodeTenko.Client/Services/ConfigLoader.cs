using System.IO;
using System.Text.Json;
using BarcodeTenko.Client.Models;

namespace BarcodeTenko.Client.Services;

public static class ConfigLoader
{
    public static AppConfig Load()
    {
        // 配置先の appsettings.json があればそれを優先し、なければビルド時に埋め込んだ既定値を使う
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var json = File.Exists(path) ? File.ReadAllText(path) : LoadEmbeddedJson();

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var config = JsonSerializer.Deserialize<AppConfig>(json, options)
            ?? throw new InvalidOperationException("設定の読み込みに失敗しました。");

        if (string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            throw new InvalidOperationException("appsettings.json の ServerUrl が設定されていません。");
        }

        config.DataDirectory = ResolvePath(config.DataDirectory);
        config.OutputDirectory = ResolvePath(config.OutputDirectory);
        return config;
    }

    private static string LoadEmbeddedJson()
    {
        var assembly = typeof(ConfigLoader).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("埋め込まれた既定設定 (appsettings.json) が見つかりません。");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ResolvePath(string path)
        => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
}
