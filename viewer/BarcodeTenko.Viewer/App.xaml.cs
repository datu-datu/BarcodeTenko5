using System.Text;
using System.Windows;
using BarcodeTenko.Viewer.Models;
using BarcodeTenko.Viewer.Services;

namespace BarcodeTenko.Viewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 名簿 CSV の Shift_JIS(cp932) デコードに必要
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        ViewerConfig config;
        try
        {
            config = ConfigLoader.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "設定の読み込みに失敗しました", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        ViewerApiClient api;
        try
        {
            api = new ViewerApiClient(config);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "接続設定エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        List<RosterEntry> roster;
        try
        {
            using var stream = typeof(App).Assembly.GetManifestResourceStream("BarcodeTenko.Viewer.Roster.csv")
                ?? throw new InvalidOperationException("組み込み名簿CSVが見つかりません。");
            roster = RosterCsvLoader.Load(stream);
        }
        catch (Exception ex)
        {
            roster = new List<RosterEntry>();
            MessageBox.Show("組み込み名簿を読み込めませんでした: " + ex.Message, "名簿CSV", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var main = new MainWindow(config, api, roster);
        MainWindow = main;
        main.Show();
    }
}
