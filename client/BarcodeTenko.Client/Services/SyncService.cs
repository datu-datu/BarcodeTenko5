namespace BarcodeTenko.Client.Services;

/// <summary>
/// 未送信スキャンの再送ループと取消要求の送信を担う。
/// サーバへ届かない場合はローカルに残したまま指数バックオフ的に再試行する。
/// </summary>
public sealed class SyncService
{
    private const string LastSessionIdKey = "last_session_id";

    private readonly LocalStore _store;
    private readonly ApiClient _api;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SyncService(LocalStore store, ApiClient api)
    {
        _store = store;
        _api = api;
        if (int.TryParse(_store.GetValue(LastSessionIdKey), out var sessionId))
        {
            LastSessionId = sessionId;
        }
    }

    public event Action? StateChanged;

    public bool LastSyncFailed { get; private set; }
    public DateTimeOffset? LastSyncAt { get; private set; }

    /// <summary>スキャン受付レスポンスから得た、直近のサーバセッションID (点呼完了報告用)</summary>
    public int? LastSessionId { get; private set; }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    public void RequestSync()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // すでに送信要求済み
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(ct).ConfigureAwait(false);
                LastSyncFailed = false;
                LastSyncAt = DateTimeOffset.Now;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                LastSyncFailed = true;
            }

            StateChanged?.Invoke();

            var delay = LastSyncFailed ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(3);
            try
            {
                await Task.WhenAny(Task.Delay(delay, ct), _signal.WaitAsync(ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void RecordSession(Models.ScanResponse? response)
    {
        if (response?.SessionId is not int sessionId || LastSessionId == sessionId)
        {
            return;
        }

        LastSessionId = sessionId;
        _store.SetValue(LastSessionIdKey, sessionId.ToString());
    }

    public async Task SyncOnceAsync(CancellationToken ct = default)
    {
        foreach (var record in _store.GetUnsent(100))
        {
            var response = await _api.SendScanAsync(record, ct).ConfigureAwait(false);
            RecordSession(response);
            _store.MarkSent(record.Id);
        }

        foreach (var clientScanId in _store.GetPendingCancels())
        {
            await _api.CancelAsync(clientScanId, ct).ConfigureAwait(false);
            _store.RemovePendingCancel(clientScanId);
        }
    }

    /// <summary>
    /// 未送信スキャンおよび取消要求が空になるまで、すべて即座に送信し切る。
    /// 点呼完了時の一括同期に使用する。
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var unsent = _store.GetUnsent(100);
            if (unsent.Count == 0)
            {
                break;
            }

            foreach (var record in unsent)
            {
                var response = await _api.SendScanAsync(record, ct).ConfigureAwait(false);
                RecordSession(response);
                _store.MarkSent(record.Id);
            }
        }

        foreach (var clientScanId in _store.GetPendingCancels())
        {
            await _api.CancelAsync(clientScanId, ct).ConfigureAwait(false);
            _store.RemovePendingCancel(clientScanId);
        }
    }
}
