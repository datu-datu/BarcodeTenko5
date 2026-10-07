using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BarcodeTenko.Offline.Models;
using BarcodeTenko.Offline.Services;
using BarcodeTenko.Offline.ViewModels;

namespace BarcodeTenko.Offline;

public partial class MainWindow : Window
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
    private static readonly Brush CancelBrush = new SolidColorBrush(Color.FromRgb(0xCA, 0x50, 0x10));

    private readonly AppConfig _config;
    private readonly LocalStore _store;
    private readonly IReadOnlyList<Location> _locations;
    private Location? _location;
    private readonly ObservableCollection<ScanRow> _rows = new();
    private int _lastStudentNumber = -1;
    private int _pendingCount;

    public MainWindow(AppConfig config, LocalStore store, Location location, IReadOnlyList<Location> locations)
    {
        InitializeComponent();

        _config = config;
        _store = store;
        _location = location;
        _locations = locations;

        LocationText.Text = location.Name;
        ScanGrid.ItemsSource = _rows;

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            RefreshRecent();
            RewriteLiveBin();

            ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
            var clockTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            clockTimer.Tick += (_, _) =>
            {
                ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
            };
            clockTimer.Start();
        };

        Closed += (_, _) =>
        {
            Application.Current.Shutdown();
        };
    }

    private void SetLastScan(string status, Brush statusColor, string number = "")
    {
        LastScanStatusText.Text = status;
        LastScanStatusText.Foreground = statusColor;
        LastScanNumberText.Text = number;
        LastScanNumberText.Foreground = statusColor == ErrorBrush ? ErrorBrush : (Brush)FindResource("TextPrimaryBrush");
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
        if (_location is null)
        {
            SetLastScan("場所未選択", ErrorBrush);
            PlaySound(System.Media.SystemSounds.Exclamation);
            InputBox.Clear();
            InputBox.Focus();
            return;
        }

        var studentNumber = CodeNormalizer.Normalize(raw);
        if (studentNumber is null)
        {
            SetLastScan("入力エラー", ErrorBrush);
            PlaySound(System.Media.SystemSounds.Exclamation);
            InputBox.Clear();
            InputBox.Focus();
            return;
        }

        // 2回連続で同一学籍番号が入力された場合はスキップ
        if (studentNumber.Value == _lastStudentNumber)
        {
            SetLastScan("重複スキップ", CancelBrush, $"{studentNumber.Value:D5}");
            InputBox.Clear();
            InputBox.Focus();
            return;
        }

        _lastStudentNumber = studentNumber.Value;

        // 音とUIの更新を最優先
        PlaySuccessSound();
        SetLastScan("受付完了", SuccessBrush, $"{studentNumber.Value:D5}");
        InputBox.Clear();
        InputBox.Focus();

        var record = new ScanRecord
        {
            StudentNumber = studentNumber.Value,
            LocationId = _location.Id,
            LocationName = _location.Name,
            CreatedAt = DateTimeOffset.Now
        };

        var id = _store.AddScan(record);
        AppendLiveBin(studentNumber.Value);

        AddRecentRow(record, id);
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
                Console.Beep(1768, 70);
            }
            catch
            {
                PlaySound(System.Media.SystemSounds.Asterisk);
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

    private void RefreshRecent(long? newlyAddedId = null)
    {
        var query = SearchBox.Text.Trim();
        var records = string.IsNullOrEmpty(query)
            ? _store.GetPending()
            : _store.SearchScans(query);

        _rows.Clear();
        foreach (var record in records)
        {
            var row = new ScanRow(record)
            {
                IsRecentlyAdded = record.Id == newlyAddedId
            };
            _rows.Add(row);
        }

        UpdateTotalText();
    }

    private void UpdateTotalText()
    {
        _pendingCount = _store.CountPending();
        TotalCountText.Text = _pendingCount.ToString();
    }

    /// <summary>
    /// 受付1件を一覧の先頭に差し込む高速パス。
    /// 一覧は id 降順なので先頭追加で順序が保たれる。全件クエリを避けるため、
    /// 検索中（絞り込み結果が変わり得る）のときだけ RefreshRecent にまわす。
    /// </summary>
    private void AddRecentRow(ScanRecord record, long id)
    {
        if (!string.IsNullOrEmpty(SearchBox.Text.Trim()))
        {
            RefreshRecent(id);
            return;
        }

        foreach (var row in _rows)
        {
            row.IsRecentlyAdded = false;
        }

        _rows.Insert(0, new ScanRow(record) { IsRecentlyAdded = true });

        _pendingCount++;
        TotalCountText.Text = _pendingCount.ToString();
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
    }

    /// <summary>
    /// 未確定の点呼データを作業中 bin (bin/tenko_live.bin) に全件書き直す。
    /// 取消・全削除・起動時・点呼完了の直前に呼ぶ。
    /// </summary>
    private void RewriteLiveBin()
    {
        try
        {
            var numbers = _store.GetPendingStudentNumbers();
            BinWriter.WriteLive(numbers, _config.OutputDirectory);
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            ShowError("bin 書き出し失敗: " + ex.Message);
        }
    }

    /// <summary>
    /// スキャン1件を作業中 bin の末尾に追記する (全件書き直しを避ける高速パス)。
    /// </summary>
    private void AppendLiveBin(int studentNumber)
    {
        try
        {
            BinWriter.AppendLive(studentNumber, _config.OutputDirectory);
        }
        catch (Exception ex)
        {
            ShowError("bin 書き出し失敗: " + ex.Message);
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
        _store.SaveLocation(_location);
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

        _store.DeleteScan(record.Id);
        RewriteLiveBin();

        _lastStudentNumber = -1;

        SetLastScan("取消完了", CancelBrush, $"{record.StudentNumber:D5}");
        RefreshRecent();
        InputBox.Focus();
    }

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        var pending = _store.GetPendingExport();
        if (pending.Count == 0)
        {
            MessageBox.Show("出力する点呼データがありません。", "点呼完了", MessageBoxButton.OK, MessageBoxImage.Information);
            InputBox.Focus();
            return;
        }

        var message = $"{pending.Count} 件の点呼データを確定し、提出用ファイルを出力します。\nよろしいですか？";
        if (MessageBox.Show(message, "点呼完了", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            InputBox.Focus();
            return;
        }

        try
        {
            // 念のため作業中 bin を最新化してからリネームする
            RewriteLiveBin();
            var path = BinWriter.FinalizeLive(_config.OutputDirectory, _location?.Name ?? "不明");
            _store.MarkCompleted(pending.Select(r => r.Id), path);

            SetLastScan("", (Brush)FindResource("TextSecondaryBrush"));
            RefreshRecent();
            BinWriter.RevealInExplorer(path);
            MessageBox.Show($"ありがとうございます。このファイルを提出してください。 \n{path}\n", "点呼完了", MessageBoxButton.OK, MessageBoxImage.Information);

            // 1. client.db 上の点呼場所をクリア (次回起動時にも選択画面が出るようにする)
            _store.ClearLocation();

            // 2. 次の点呼場所を選択する画面を表示
            var selectWindow = new LocationSelectWindow(_locations)
            {
                Owner = this
            };

            if (selectWindow.ShowDialog() == true && selectWindow.SelectedLocation is not null)
            {
                // 新しい場所が選択された場合：状態を更新して次の点呼待機
                _location = selectWindow.SelectedLocation;
                _store.SaveLocation(_location);
                LocationText.Text = _location.Name;
            }
            else
            {
                _location = null;
                LocationText.Text = "点呼場所を選択してください";
            }
            _lastStudentNumber = -1;
            SetLastScan("", (Brush)FindResource("TextSecondaryBrush"));
            RefreshRecent();
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
        var pendingCount = _store.CountPending();
        if (pendingCount == 0)
        {
            MessageBox.Show("削除対象の未確定データはありません。", "点呼データの削除", MessageBoxButton.OK, MessageBoxImage.Information);
            InputBox.Focus();
            return;
        }

        var answer = MessageBox.Show(
            $"現在表示中の未完了点呼データ（{pendingCount}件）をすべて削除しますか？\n\n※ この操作は元に戻せません。",
            "点呼データの削除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            InputBox.Focus();
            return;
        }

        try
        {
            _store.DeletePending();
            RewriteLiveBin();

            _lastStudentNumber = -1;

            SetLastScan("", (Brush)FindResource("TextSecondaryBrush"));
            RefreshRecent();
        }
        catch (Exception ex)
        {
            MessageBox.Show("削除処理に失敗しました: " + ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        InputBox.Focus();
    }
}
