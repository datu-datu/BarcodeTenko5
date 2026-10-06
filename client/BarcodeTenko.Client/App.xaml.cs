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
        // オフライン時は無言でフォールバックせず、再取得の機会を与える。
        List<Location> locations = new();
        while (true)
        {
            try
            {
                locations = Task.Run(() => api.GetLocationsAsync()).GetAwaiter().GetResult();
                break;
            }
            catch
            {
                var choice = MessageBox.Show(
                    "サーバから点呼場所を取得できませんでした。再試行しますか？\n\n" +
                    "・「はい」: 再試行\n" +
                    "・「いいえ」: オフラインで続行\n",
                    "サーバに接続できません",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Yes);

                if (choice == MessageBoxResult.Yes)
                {
                    continue;
                }
                if (choice == MessageBoxResult.Cancel)
                {
                    Shutdown();
                    return;
                }

                // 「いいえ」: オフラインで続行し、設定ファイルの内容にフォールバックする
                break;
            }
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

        // 前回選択した点呼場所が client.db に記録されていれば選択画面をスキップする。
        // サーバ側の一覧に存在しない場所が記録されていた場合は選択画面に戻す。
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
