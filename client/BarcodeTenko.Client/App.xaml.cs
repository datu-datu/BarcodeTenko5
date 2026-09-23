using System.Windows;
using BarcodeTenko.Client.Models;
using BarcodeTenko.Client.Services;

namespace BarcodeTenko.Client;

public partial class App : Application
{
    private SyncService? _sync;

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
        if (string.IsNullOrWhiteSpace(config.ClientId))
        {
            config.ClientId = store.GetOrCreateClientId();
        }

        var api = new ApiClient(config);

        // 点呼場所はサーバを正とする。取得できなければ設定ファイルの内容にフォールバック。
        List<Location> locations;
        try
        {
            locations = Task.Run(() => api.GetLocationsAsync()).GetAwaiter().GetResult();
        }
        catch
        {
            locations = new List<Location>();
        }
        if (locations.Count == 0)
        {
            locations = config.Locations;
        }

        if (locations.Count == 0)
        {
            MessageBox.Show(
                "点呼場所が取得できませんでした。サーバに接続できるか、appsettings.json の Locations を確認してください。",
                "点呼場所なし",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // 前回選択した点呼場所が data フォルダーに記録されていれば選択画面をスキップする。
        // サーバ側の一覧に存在しない場所が記録されていた場合は選択画面に戻す。
        var saved = LocationStore.Load(config);
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
            LocationStore.Save(config, selected);
        }

        _sync = new SyncService(store, api);
        _sync.Start();

        var main = new MainWindow(config, store, api, _sync, selected, locations);
        this.MainWindow = main;
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _sync?.Stop();
        base.OnExit(e);
    }
}
