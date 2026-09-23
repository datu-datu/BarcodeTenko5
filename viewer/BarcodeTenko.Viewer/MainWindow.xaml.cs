using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BarcodeTenko.Viewer.Models;
using BarcodeTenko.Viewer.Services;
using Microsoft.Win32;

namespace BarcodeTenko.Viewer;

public partial class MainWindow : Window
{
    private enum SourceMode
    {
        Server,
        File
    }

    private static readonly TimeSpan HighlightDuration = TimeSpan.FromSeconds(5);

    private readonly ViewerConfig _config;
    private readonly ViewerApiClient _api;
    private readonly ObservableCollection<ViewerRow> _rows = new();
    private readonly DispatcherTimer _highlightTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private Dictionary<int, RosterEntry> _rosterByNumber = new();
    private List<ViewerRow> _allRows = new();
    private SummaryResponse? _summary;
    private string? _binPath;
    private string _rosterName = "組み込み名簿";
    private SourceMode _mode = SourceMode.Server;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private long _streamGeneration;
    private long _snapshotGeneration;
    private int? _sessionId;
    private bool _loggedIn;
    private bool _closed;
    private Task? _snapshotTask;

    public MainWindow(ViewerConfig config, ViewerApiClient api, List<RosterEntry> roster)
    {
        InitializeComponent();

        _config = config;
        _api = api;
        SetRoster(roster, _rosterName);
        ScanGrid.ItemsSource = _rows;
        _highlightTimer.Tick += OnHighlightTick;
        _highlightTimer.Start();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateModeButtons();
        StartClock();

        if (!string.IsNullOrWhiteSpace(_config.AdminPassword))
        {
            StatusText.Text = "設定されたパスワードでログイン中...";
            var result = await _api.LoginAsync(_config.AdminPassword);
            if (result.Outcome == LoginOutcome.Success)
            {
                _loggedIn = true;
                StartStream();
                return;
            }

            StatusText.Text = "自動ログイン失敗: " + result.Message;
            if (PromptLogin(result.Message))
            {
                StartStream();
            }
            else
            {
                StatusText.Text = "未ログインです。「ログイン」を押してください。";
            }
            return;
        }

        try
        {
            if (await _api.CheckSessionAsync())
            {
                _loggedIn = true;
                StartStream();
                return;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "サーバー接続失敗: " + ex.Message;
            return;
        }

        if (PromptLogin())
        {
            StartStream();
        }
        else
        {
            StatusText.Text = "未ログインです。「ログイン」を押してください。";
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        StopStream();
        if (_streamTask is not null)
        {
            try { await _streamTask; }
            catch (OperationCanceledException) { }
        }
        _streamCts?.Dispose();
        _api.Dispose();
        Application.Current.Shutdown();
    }

    private void StartClock()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        timer.Start();
    }

    private void UpdateModeButtons()
    {
        var server = _mode == SourceMode.Server;
        RefreshButton.IsEnabled = server;
        LoginButton.IsEnabled = server;
        OpenBinButton.IsEnabled = !server;
    }

    // --- ソース切替 ---

    private void ServerMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _mode = SourceMode.Server;
        UpdateModeButtons();
        _ = ConnectServerAsync();
    }

    private async Task ConnectServerAsync()
    {
        if (_loggedIn)
        {
            StartStream();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_config.AdminPassword))
        {
            var result = await _api.LoginAsync(_config.AdminPassword);
            if (result.Outcome == LoginOutcome.Success)
            {
                _loggedIn = true;
                StartStream();
                return;
            }

            StatusText.Text = "自動ログイン失敗: " + result.Message;
            if (PromptLogin(result.Message)) StartStream();
            return;
        }

        try
        {
            if (await _api.CheckSessionAsync())
            {
                _loggedIn = true;
                StartStream();
            }
            else
            {
                await LoginAndStartStreamAsync();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "サーバー接続失敗: " + ex.Message;
        }
    }

    private void FileMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _mode = SourceMode.File;
        StopStream();
        UpdateModeButtons();
        UpdateSummaryPanel();
        StatusText.Text = "ファイル表示中";
    }

    // --- サーバーモード ---

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != SourceMode.Server || _snapshotTask is { IsCompleted: false })
        {
            return;
        }

        StopStream();
        var generation = _streamGeneration;
        try
        {
            if (!await _api.CheckSessionAsync())
            {
                _loggedIn = false;
                if (!string.IsNullOrWhiteSpace(_config.AdminPassword))
                {
                    var login = await _api.LoginAsync(_config.AdminPassword);
                    _loggedIn = login.Outcome == LoginOutcome.Success;
                    if (!_loggedIn) StatusText.Text = "自動ログイン失敗: " + login.Message;
                }
                if (!_loggedIn) _loggedIn = PromptLogin();
            }

            if (_loggedIn) await SyncServerSnapshotAsync(generation, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText.Text = "一覧同期失敗: " + ex.Message;
        }
        finally
        {
            if (_loggedIn && _mode == SourceMode.Server) StartStream();
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == SourceMode.Server && await LoginAndStartStreamAsync())
        {
            StatusText.Text = "ログインしました。SSE接続中...";
        }
    }

    private async Task<bool> LoginAndStartStreamAsync()
    {
        if (PromptLogin())
        {
            StartStream();
            return true;
        }

        StatusText.Text = "未ログインです。「ログイン」を押してください。";
        return false;
    }

    private bool PromptLogin(string? initialMessage = null)
    {
        var window = new LoginWindow(_api, initialMessage) { Owner = this };
        var ok = window.ShowDialog() == true;
        _loggedIn = ok;
        return ok;
    }

    private void StartStream()
    {
        if (_mode != SourceMode.Server || !_loggedIn)
        {
            return;
        }

        if (_streamTask is { IsCompleted: false })
        {
            if (_streamCts?.IsCancellationRequested != true)
            {
                return;
            }

            _ = RestartStreamAfterStopAsync(_streamTask);
            return;
        }

        BeginStream();
    }

    private async Task RestartStreamAfterStopAsync(Task previousStream)
    {
        try { await previousStream; }
        catch (OperationCanceledException) { }

        if (!_closed && _mode == SourceMode.Server && _loggedIn)
        {
            BeginStream();
        }
    }

    private void BeginStream()
    {
        _streamCts?.Dispose();
        _streamCts = new CancellationTokenSource();
        var generation = ++_streamGeneration;
        _streamTask = RunStreamAsync(generation, _streamCts.Token);
    }

    private void StopStream()
    {
        _streamGeneration++;
        _streamCts?.Cancel();
    }

    private async Task RunStreamAsync(long generation, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && generation == _streamGeneration)
        {
            var needsSnapshot = true;
            try
            {
                StatusText.Text = "SSE接続中...";
                await foreach (var serverEvent in _api.ReadEventsAsync(ct))
                {
                    if (generation != _streamGeneration || _mode != SourceMode.Server)
                    {
                        return;
                    }

                    var initialStats = needsSnapshot && serverEvent.Name == "stats";
                    await Dispatcher.InvokeAsync(() => HandleServerEventAsync(serverEvent, generation, ct, initialStats))
                        .Task.Unwrap();
                    if (initialStats)
                    {
                        needsSnapshot = false;
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                throw new IOException("SSE接続が終了しました。");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ApiConfigurationException ex)
            {
                await Dispatcher.InvokeAsync(() => StatusText.Text = ex.Message);
                return;
            }
            catch (ApiAuthException ex)
            {
                var reauthenticated = await Dispatcher.InvokeAsync(async () =>
                {
                    _loggedIn = false;
                    StatusText.Text = "認証が失効しました: " + ex.Message;
                    if (!string.IsNullOrWhiteSpace(_config.AdminPassword))
                    {
                        var result = await _api.LoginAsync(_config.AdminPassword);
                        _loggedIn = result.Outcome == LoginOutcome.Success;
                        if (!_loggedIn) StatusText.Text = "自動再ログイン失敗: " + result.Message;
                    }

                    if (!_loggedIn && _mode == SourceMode.Server) _loggedIn = PromptLogin();
                    return _loggedIn;
                }).Task.Unwrap();

                if (!reauthenticated || ct.IsCancellationRequested)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() => StatusText.Text = "SSE切断: " + ex.Message + " (5秒後に再接続)");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task HandleServerEventAsync(ServerEvent serverEvent, long generation, CancellationToken ct, bool initialStats)
    {
        if (generation != _streamGeneration || _mode != SourceMode.Server)
        {
            return;
        }

        switch (serverEvent.Name)
        {
            case "stats":
            case "session":
                var summary = System.Text.Json.JsonSerializer.Deserialize<SummaryResponse>(serverEvent.Data,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var oldSessionId = _sessionId;
                _summary = summary;
                _sessionId = summary?.Session?.Id;
                UpdateSummaryPanel();
                if (initialStats || serverEvent.Name == "session" && oldSessionId != _sessionId)
                {
                    await SyncServerSnapshotAsync(generation, ct, summary, useKnownSummary: true);
                }
                break;

            case "scan":
                var scan = System.Text.Json.JsonSerializer.Deserialize<ScanEvent>(serverEvent.Data,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (scan is not null && scan.SessionId == _sessionId)
                {
                    AddScan(scan);
                }
                break;

            case "cancel":
                var cancel = System.Text.Json.JsonSerializer.Deserialize<CancelEvent>(serverEvent.Data,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cancel is not null && cancel.SessionId == _sessionId)
                {
                    RemoveScan(cancel.Id);
                }
                break;
        }
    }

    private async Task SyncServerSnapshotAsync(
        long generation,
        CancellationToken ct,
        SummaryResponse? knownSummary = null,
        bool useKnownSummary = false)
    {
        if (_snapshotTask is { IsCompleted: false })
        {
            if (_snapshotGeneration == generation)
            {
                await _snapshotTask;
                return;
            }

            await _snapshotTask;
        }

        if (generation != _streamGeneration || _mode != SourceMode.Server)
        {
            return;
        }

        _snapshotGeneration = generation;
        _snapshotTask = FetchServerSnapshotAsync(generation, ct, knownSummary, useKnownSummary);
        await _snapshotTask;
    }

    private async Task FetchServerSnapshotAsync(
        long generation,
        CancellationToken ct,
        SummaryResponse? knownSummary,
        bool useKnownSummary)
    {
        if (generation != _streamGeneration || _mode != SourceMode.Server)
        {
            return;
        }

        try
        {
            StatusText.Text = "受付一覧を同期中...";
            var summary = useKnownSummary ? knownSummary : await _api.GetSummaryAsync(ct);
            var sessionId = summary?.Session?.Id;
            var scans = await _api.GetScansAsync(sessionId, includeDeleted: false, ct);
            if (generation != _streamGeneration || _mode != SourceMode.Server)
            {
                return;
            }

            _summary = summary;
            _sessionId = sessionId;
            ReplaceServerRows(scans);
            UpdateSummaryPanel();
            LastUpdatedText.Text = DateTime.Now.ToString("MM/dd HH:mm:ss");
            StatusText.Text = $"SSE接続中 / {_allRows.Count}件同期済み";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "一覧同期失敗: " + ex.Message;
            if (ex is ApiAuthException) throw;
        }
    }

    private void ReplaceServerRows(List<AttendanceRow> scans)
    {
        var rows = scans.Select(scan =>
        {
            var row = CreateRow(scan, false);
            ApplyRoster(row);
            return row;
        }).ToList();
        _allRows = rows;
        RenderRows(reset: true);
    }

    private void AddScan(ScanEvent scan)
    {
        if (_allRows.Any(row => row.AttendanceId == scan.Id))
        {
            return;
        }

        var row = new ViewerRow
        {
            AttendanceId = scan.Id,
            StudentNumber = scan.StudentNumber,
            TimeText = FormatTime(scan.ReceivedAt),
            LocationName = string.IsNullOrEmpty(scan.LocationName) ? "場所未選択" : scan.LocationName!,
            IsRecentlyAdded = true,
            HighlightUntil = DateTime.Now + HighlightDuration
        };
        ApplyRoster(row);
        _allRows.Insert(0, row);
        ReconcileRows();
        UpdateSummaryPanel();
        LastUpdatedText.Text = DateTime.Now.ToString("MM/dd HH:mm:ss");
    }

    private void RemoveScan(long attendanceId)
    {
        _allRows.RemoveAll(row => row.AttendanceId == attendanceId);
        ReconcileRows();
        UpdateSummaryPanel();
        LastUpdatedText.Text = DateTime.Now.ToString("MM/dd HH:mm:ss");
    }

    private void OnHighlightTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        foreach (var row in _allRows)
        {
            if (row.IsRecentlyAdded && now >= row.HighlightUntil)
            {
                row.IsRecentlyAdded = false;
            }
        }
    }

    private static ViewerRow CreateRow(AttendanceRow scan, bool recentlyAdded)
        => new()
        {
            AttendanceId = scan.Id,
            StudentNumber = scan.StudentNumber,
            TimeText = FormatTime(scan.ReceivedAt),
            LocationName = string.IsNullOrEmpty(scan.LocationName) ? "場所未選択" : scan.LocationName!,
            IsRecentlyAdded = recentlyAdded
        };

    // --- bin ファイルモード ---

    private void OpenBin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "点呼 bin ファイル (*.bin)|*.bin|すべてのファイル (*.*)|*.*"
        };
        if (Directory.Exists(_config.BinDirectory))
        {
            dialog.InitialDirectory = _config.BinDirectory;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var numbers = BinReader.ReadNumbers(dialog.FileName);
            var (location, time) = BinReader.ParseFileName(dialog.FileName);
            var timeText = time?.ToString("MM/dd HH:mm:ss") ?? "";

            _allRows = numbers.Select((number, index) =>
            {
                var row = new ViewerRow
                {
                    AttendanceId = -(index + 1L),
                    StudentNumber = number,
                    TimeText = timeText,
                    LocationName = location ?? ""
                };
                ApplyRoster(row);
                return row;
            }).ToList();

            _summary = null;
            _binPath = dialog.FileName;
            RenderRows(reset: true);
            UpdateSummaryPanel();
            LastUpdatedText.Text = DateTime.Now.ToString("MM/dd HH:mm:ss");
            StatusText.Text = $"bin: {Path.GetFileName(dialog.FileName)} ({numbers.Count}件)";
        }
        catch (Exception ex)
        {
            MessageBox.Show("bin ファイルの読み込みに失敗しました: " + ex.Message, "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- 名簿 CSV ---

    private void LoadCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "名簿 CSV (*.csv)|*.csv|すべてのファイル (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var entries = RosterCsvLoader.Load(dialog.FileName);
            SetRoster(entries, Path.GetFileName(dialog.FileName));
            UpdateVisibleRoster();
            StatusText.Text = $"名簿を読み込みました ({entries.Count}件)";
        }
        catch (Exception ex)
        {
            MessageBox.Show("CSV の読み込みに失敗しました: " + ex.Message, "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetRoster(List<RosterEntry> entries, string name)
    {
        _rosterByNumber = entries
            .GroupBy(entry => entry.StudentNumber)
            .ToDictionary(group => group.Key, group => group.First());
        _rosterName = name;
        RosterFileText.Text = name;
        RosterCountText.Text = $"{entries.Count} 件";
    }

    private void ApplyRoster(ViewerRow row)
    {
        if (_rosterByNumber.TryGetValue(row.StudentNumber, out var entry))
        {
            row.ClassName = entry.ClassName;
            row.Name = entry.Name;
        }
    }

    private void UpdateVisibleRoster()
    {
        foreach (var row in _allRows)
        {
            row.ClassName = "";
            row.Name = "";
            ApplyRoster(row);
        }

        ReconcileRows();
    }

    // --- 一覧描画 / 検索 ---

    private void RenderRows(bool reset = false)
    {
        if (reset)
        {
            _rows.Clear();
        }

        ReconcileRows();
    }

    private void ReconcileRows()
    {
        var desired = _allRows.Where(row => Matches(row, SearchBox.Text.Trim())).ToList();
        var byId = _rows.Where(row => row.AttendanceId is not null)
            .ToDictionary(row => row.AttendanceId!.Value);
        var desiredIds = desired.Where(row => row.AttendanceId is not null).Select(row => row.AttendanceId!.Value).ToHashSet();

        for (var index = _rows.Count - 1; index >= 0; index--)
        {
            var current = _rows[index];
            if (current.AttendanceId is long id && !desiredIds.Contains(id))
            {
                _rows.RemoveAt(index);
            }
        }

        for (var index = 0; index < desired.Count; index++)
        {
            var wanted = desired[index];
            if (wanted.AttendanceId is long id && byId.TryGetValue(id, out var existing) && _rows.Contains(existing))
            {
                existing.CopyFrom(wanted);
                var currentIndex = _rows.IndexOf(existing);
                if (currentIndex != index)
                {
                    _rows.Move(currentIndex, Math.Min(index, _rows.Count - 1));
                }
            }
            else if (index >= _rows.Count || !ReferenceEquals(_rows[index], wanted))
            {
                _rows.Insert(index, wanted);
            }
        }

        while (_rows.Count > desired.Count)
        {
            _rows.RemoveAt(_rows.Count - 1);
        }

        ResultCountText.Text = _rows.Count == _allRows.Count
            ? $"{_rows.Count} 件"
            : $"{_rows.Count} / {_allRows.Count} 件";
    }

    private static bool Matches(ViewerRow row, string query)
        => string.IsNullOrEmpty(query)
            || row.ClassName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.StudentNumberText.Contains(query, StringComparison.Ordinal)
            || row.LocationName.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        ClearSearchButton.Visibility = string.IsNullOrEmpty(query) ? Visibility.Collapsed : Visibility.Visible;
        ReconcileRows();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => SearchBox.Clear();

    // --- サマリーカード ---

    private void UpdateSummaryPanel()
    {
        if (_mode == SourceMode.File)
        {
            SessionText.Text = "bin ファイル";
            RateText.Text = "—";
            TotalText.Text = $"{_allRows.Count} 件";
            SourceText.Text = Path.GetFileName(_binPath ?? "");
            return;
        }

        var session = _summary?.Session;
        SessionText.Text = session is null ? "セッション未設定" : $"{session.Name} ({StatusLabel(session.Status)})";
        RateText.Text = _summary?.Rate is double rate ? $"{rate * 100:F1}%" : "—";
        TotalText.Text = $"{_summary?.Total ?? 0} / {_summary?.Target ?? 0}";
        SourceText.Text = session?.Name ?? "—";
    }

    private static string StatusLabel(string status) => status switch
    {
        "open" => "実施中",
        "closed" => "終了",
        "idle" => "待機",
        _ => status
    };

    private static string FormatTime(string? iso)
    {
        if (string.IsNullOrEmpty(iso))
        {
            return "";
        }

        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
        {
            return iso;
        }

        return value.ToLocalTime().ToString("MM/dd HH:mm:ss");
    }
}
