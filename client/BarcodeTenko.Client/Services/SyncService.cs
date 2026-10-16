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
    private readonly SemaphoreSlim _syncLock = new(1, 1);
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

    public void ClearLastSession()
    {
        LastSessionId = null;
        _store.DeleteValue(LastSessionIdKey);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // SyncOnceAsync 内で LastSyncFailed 更新・StateChanged 発火済み
            }

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
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SendBatchCoreAsync(ct).ConfigureAwait(false);
            LastSyncFailed = false;
            LastSyncAt = DateTimeOffset.Now;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            LastSyncFailed = true;
            throw;
        }
        finally
        {
            _syncLock.Release();
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// 未送信スキャンおよび取消要求が両方とも空になるまで、排他ロック下で全て即座に送信し切る。
    /// 点呼完了時の一括同期に使用する。
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var sentAny = await SendBatchCoreAsync(ct).ConfigureAwait(false);
                if (!sentAny)
                {
                    break;
                }
            }
            LastSyncFailed = false;
            LastSyncAt = DateTimeOffset.Now;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            LastSyncFailed = true;
            throw;
        }
        finally
        {
            _syncLock.Release();
            StateChanged?.Invoke();
        }
    }

    private async Task<bool> SendBatchCoreAsync(CancellationToken ct)
    {
        var progressed = false;

        var unsent = _store.GetUnsent(100);
        foreach (var record in unsent)
        {
            var response = await _api.SendScanAsync(record, ct).ConfigureAwait(false);
            RecordSession(response);
            if (!_store.MarkSent(record.Id))
            {
                // 送信中にUI側で取消・削除された場合はサーバ側も取り消すためキューへ積む
                _store.EnqueueCancel(record.ClientScanId);
            }
            progressed = true;
        }

        var cancels = _store.GetPendingCancels();
        foreach (var clientScanId in cancels)
        {
            await _api.CancelAsync(clientScanId, ct).ConfigureAwait(false);
            _store.RemovePendingCancel(clientScanId);
            progressed = true;
        }

        return progressed;
    }
}
