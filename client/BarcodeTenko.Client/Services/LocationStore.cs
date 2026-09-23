using System.IO;
using System.Text.Json;
using BarcodeTenko.Client.Models;

namespace BarcodeTenko.Client.Services;

/// <summary>
/// 選択した点呼場所を data フォルダー (location.json) に記録する。
/// 次回起動時にこれを読み、点呼場所の選択画面をスキップする。
/// </summary>
public static class LocationStore
{
    private static string PathFor(AppConfig config) => Path.Combine(config.DataDirectory, "location.json");

    public static Location? Load(AppConfig config)
    {
        try
        {
            var path = PathFor(config);
            if (!File.Exists(path))
            {
                return null;
            }

            var location = JsonSerializer.Deserialize<Location>(File.ReadAllText(path));
            return location is { Id: > 0 } ? location : null;
        }
        catch
        {
            // 壊れたファイルは無視して選択画面に戻す
            return null;
        }
    }

    public static void Save(AppConfig config, Location location)
    {
        Directory.CreateDirectory(config.DataDirectory);
        File.WriteAllText(PathFor(config), JsonSerializer.Serialize(location));
    }
}
