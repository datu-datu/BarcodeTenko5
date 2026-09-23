using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BarcodeTenko.Client.Models;
using BarcodeTenko.Client.Services;
using BarcodeTenko.Client.ViewModels;

namespace BarcodeTenko.Client;

public partial class MainWindow : Window
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
    private static readonly Brush CancelBrush = new SolidColorBrush(Color.FromRgb(0xCA, 0x50, 0x10));

    private readonly AppConfig _config;
    private readonly LocalStore _store;
    private readonly ApiClient _api;
    private readonly SyncService _sync;
    private readonly IReadOnlyList<Location> _locations;
    private Location _location;
    private readonly ObservableCollection<ScanRow> _rows = new();
    private int _lastStudentNumber = -1;

    public MainWindow(AppConfig config, LocalStore store, ApiClient api, SyncService sync, Location location, IReadOnlyList<Location> locations)
    {
        InitializeComponent();

        _config = config;
        _store = store;
        _api = api;
        _sync = sync;
        _location = location;
        _locations = locations;

        LocationText.Text = location.Name;
        ScanGrid.ItemsSource = _rows;

        _sync.StateChanged += OnSyncStateChanged;

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            RefreshRecent();
            UpdateSyncText();
            RewriteLiveBin();
        };

        Closed += (_, _) =>
        {
            Application.Current.Shutdown();
        };
    }

    private void OnSyncStateChanged()
    {
        Dispatcher.Invoke(() =>
        {
            RefreshRecent();
            UpdateSyncText();
        });
    }

    private void UpdateSyncText()
    {
        var unsent = _store.CountUnsent();
        if (_sync.LastSyncFailed)
        {
            SyncText.Text = unsent == 0 ? "同期済み" : $"未送信 ({unsent}件)";
        }
        else
        {
            SyncText.Text = unsent == 0 ? "同期済み" : $"同期待ち ({unsent}件)";
        }
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        ProcessInput(InputBox.Text);
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        ProcessInput(InputBox.Text);
    }

    private void ProcessInput(string raw)
    {
        var studentNumber = CodeNormalizer.Normalize(raw);
        if (studentNumber is null)
        {
            StatusMessageText.Foreground = ErrorBrush;
            StatusMessageText.Text = "5桁または10桁の数字を入力してください";
            PlaySound(System.Media.SystemSounds.Exclamation);
            InputBox.Clear();
            InputBox.Focus();
            return;
        }

        // 2回連続で同一学籍番号が入力された場合はスキップ
        if (studentNumber.Value == _lastStudentNumber)
        {
            StatusMessageText.Foreground = CancelBrush;
            StatusMessageText.Text = $"同一の学籍番号が連続したためスキップしました: {studentNumber.Value:D5}";
            InputBox.Clear();
            InputBox.Focus();
            return;
        }

        _lastStudentNumber = studentNumber.Value;

        var scanId = Guid.NewGuid().ToString("N");
        var record = new ScanRecord
        {
            ClientScanId = scanId,
            StudentNumber = studentNumber.Value,
            LocationId = _location.Id,
            LocationName = _location.Name,
            CreatedAt = DateTimeOffset.Now
        };

        _store.AddScan(record);
        _sync.RequestSync();
        RewriteLiveBin();

        StatusMessageText.Foreground = SuccessBrush;
        StatusMessageText.Text = $"受付: {studentNumber.Value:D5}";
        PlaySuccessSound();

        InputBox.Clear();
        InputBox.Focus();
        RefreshRecent(scanId);
        UpdateSyncText();
    }

    private void PlaySuccessSound()
    {
        if (!_config.SoundEnabled)
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                // (ピッ / 1768Hz, 70ms ラの音って落ち着くよねー)
                Console.Beep(1768, 70);
            }
            catch
            {
                // ビープ音非対応環境ではフォールバック
                try
                {
                    System.Media.SystemSounds.Asterisk.Play();
                }
                catch
                {
                }
            }
        });
    }

    private void PlaySound(System.Media.SystemSound sound)
    {
        if (!_config.SoundEnabled)
        {
            return;
        }

        try
        {
            sound.Play();
        }
        catch
        {
            // サウンド非対応環境では無視
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        ClearSearchButton.Visibility = string.IsNullOrEmpty(query) ? Visibility.Collapsed : Visibility.Visible;
        RefreshRecent();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        InputBox.Focus();
    }

    private void RefreshRecent(string? newlyAddedScanId = null)
    {
        var query = SearchBox.Text.Trim();
        var records = string.IsNullOrEmpty(query)
            ? _store.GetRecent(200)
            : _store.SearchScans(query);

        _rows.Clear();
        foreach (var record in records)
        {
            var row = new ScanRow(record)
            {
                IsRecentlyAdded = record.ClientScanId == newlyAddedScanId
            };
            _rows.Add(row);
        }

        UpdateTotalText();
    }

    private void UpdateTotalText()
    {
        TotalCountText.Text = _store.CountDistinctStudents().ToString();
    }

    /// <summary>
    /// 未完了の点呼データを作業中 bin (tenko_live.bin) に全件書き直す。
    /// 追加・取り消しのたびに呼び、点呼完了を押さなくても常に最新の内容を出力しておく。
    /// </summary>
    private void RewriteLiveBin()
    {
        try
        {
            var numbers = _store.GetPendingExport().Select(r => r.StudentNumber).ToList();
            BinWriter.WriteLive(numbers, _config.OutputDirectory);
        }
        catch (Exception ex)
        {
            SyncText.Text = "bin 書き出し失敗: " + ex.Message;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var selectWindow = new LocationSelectWindow(_locations) { Owner = this };
        if (selectWindow.ShowDialog() != true || selectWindow.SelectedLocation is null)
        {
            InputBox.Focus();
            return;
        }

        _location = selectWindow.SelectedLocation;
        LocationStore.Save(_config, _location);
        LocationText.Text = _location.Name;
        InputBox.Focus();
    }

    private void OpenBinFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _config.OutputDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("フォルダーを開けませんでした: " + ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        InputBox.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not ScanRow row)
        {
            return;
        }

        var record = row.Record;
        var answer = MessageBox.Show(
            $"学籍番号 {record.StudentNumber:D5} の受付を取り消しますか？",
            "取り消しの確認",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        if (record.Sent)
        {
            _store.EnqueueCancel(record.ClientScanId);
        }
        _store.DeleteScan(record.ClientScanId);
        _sync.RequestSync();
        RewriteLiveBin();

        _lastStudentNumber = -1;

        StatusMessageText.Foreground = CancelBrush;
        StatusMessageText.Text = $"取消: {record.StudentNumber:D5}";
        RefreshRecent();
        UpdateSyncText();
        InputBox.Focus();
    }

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        var pending = _store.GetPendingExport();
        if (pending.Count == 0)
        {
            MessageBox.Show("出力する点呼データがありません。", "点呼完了", MessageBoxButton.OK, MessageBoxImage.Information);
            InputBox.Focus();
            return;
        }

        // 未送信データがある場合、まず即時送信（フラッシュ）を試みる
        while (_store.CountUnsent() > 0)
        {
            CompleteButton.IsEnabled = false;
            try
            {
                await _sync.FlushAsync();
            }
            catch
            {
                // 通信エラー等は下の判定でハンドリング
            }
            finally
            {
                CompleteButton.IsEnabled = true;
                UpdateSyncText();
            }

            var unsent = _store.CountUnsent();
            if (unsent > 0)
            {
                var res = MessageBox.Show(
                    $"サーバーへ未送信のデータが {unsent} 件あります。\n\n" +
                    "「はい」: 再試行（Wi-Fi接続を確認した後に押してください）\n" +
                    "「いいえ」: 提出用ファイル（bin）は問題ないのでこの画面が繰り返し表示されたら「いいえ」で大丈夫です。\n" +
                    "「キャンセル」: 完了処理を中断",
                    "未送信データがあります",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                if (res == MessageBoxResult.Yes)
                {
                    continue;
                }
                else if (res == MessageBoxResult.No)
                {
                    break;
                }
                else
                {
                    InputBox.Focus();
                    return;
                }
            }
        }

        var message = $"{pending.Count} 件の点呼データを確定し、提出用ファイルを出力します。\nよろしいですか？";
        if (MessageBox.Show(message, "点呼完了", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            InputBox.Focus();
            return;
        }

        try
        {
            // 念のため作業中 bin を最新化してからリネームする
            RewriteLiveBin();
            var path = BinWriter.FinalizeLive(_config.OutputDirectory, _location.Name);
            _store.MarkCompleted(pending.Select(r => r.Id), path);
            RefreshRecent();
            UpdateSyncText();
            BinWriter.RevealInExplorer(path);
            MessageBox.Show($"{pending.Count} 件を確定しました。\n{path}", "点呼完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("確定に失敗しました: " + ex.Message, "点呼完了", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            InputBox.Focus();
        }
    }

    private void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "すべての点呼履歴と累計データを削除しますか？\nこの操作は元に戻せません。",
            "全履歴の削除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            InputBox.Focus();
            return;
        }

        try
        {
            _store.DeleteAll();
            _sync.RequestSync();
            RewriteLiveBin();

            _lastStudentNumber = -1;

            StatusMessageText.Foreground = CancelBrush;
            StatusMessageText.Text = "全履歴を削除しました";

            RefreshRecent();
            UpdateSyncText();
        }
        catch (Exception ex)
        {
            MessageBox.Show("削除処理に失敗しました: " + ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        InputBox.Focus();
    }
}
