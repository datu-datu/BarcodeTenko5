using System.Windows;
using BarcodeTenko.Offline.Models;
using BarcodeTenko.Offline.Services;

namespace BarcodeTenko.Offline;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppConfig config;
        try
        {
            config = ConfigLoader.Load();
            config.EnsureDirectories();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "設定の読み込みに失敗しました", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var store = new LocalStore(config);

        // オフライン版はサーバに接続しないため、点呼場所は appsettings.json を正とする。
        var locations = config.Locations;
        if (locations.Count == 0)
        {
            MessageBox.Show(
                "点呼場所が設定されていません。appsettings.json の Locations を確認してください。",
                "点呼場所なし",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // 前回選択した点呼場所が client.db に記録されていれば選択画面をスキップする。
        // 設定ファイルの一覧に存在しない場所が記録されていた場合は選択画面に戻す。
        var saved = store.LoadLocation();
        var selected = saved is not null
            ? locations.FirstOrDefault(l => l.Id == saved.Id)
            : null;

        if (selected is null)
        {
            var selectWindow = new LocationSelectWindow(locations);
            if (selectWindow.ShowDialog() != true || selectWindow.SelectedLocation is null)
            {
                Shutdown();
                return;
            }

            selected = selectWindow.SelectedLocation;
            store.SaveLocation(selected);
        }

        var main = new MainWindow(config, store, selected, locations);
        this.MainWindow = main;
        main.Show();
    }
}
