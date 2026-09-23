'use strict';

(function () {
  const el = (id) => document.getElementById(id);

  function escapeHtml(value) {
    return String(value === null || value === undefined ? '' : value).replace(/[&<>"']/g, (c) => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    }[c]));
  }

  function fmtTime(iso) {
    if (!iso) return '';
    return new Date(iso).toLocaleString('ja-JP');
  }

  function statusLabel(status) {
    return status === 'open' ? '実施中' : status === 'closed' ? '終了' : '待機';
  }

  async function api(path, options) {
    const res = await fetch('/admin/api' + path, {
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      ...options
    });
    if (res.status === 401) {
      showLogin();
      throw new Error('unauthorized');
    }
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data.error || 'HTTP ' + res.status);
    return data;
  }

  // --- 認証 ---
  function showLogin() {
    el('loginOverlay').classList.remove('hidden');
    el('password').focus();
  }
  function hideLogin() {
    el('loginOverlay').classList.add('hidden');
  }

  el('loginBtn').addEventListener('click', doLogin);
  el('password').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') doLogin();
  });

  async function doLogin() {
    el('loginError').textContent = '';
    try {
      const res = await fetch('/admin/api/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'same-origin',
        body: JSON.stringify({ password: el('password').value })
      });
      if (!res.ok) {
        const d = await res.json().catch(() => ({}));
        throw new Error(d.error || 'ログインに失敗しました');
      }
      el('password').value = '';
      hideLogin();
      await refreshAll();
      connectStream();
    } catch (err) {
      el('loginError').textContent = err.message;
    }
  }

  el('logout').addEventListener('click', async (e) => {
    e.preventDefault();
    await fetch('/admin/api/logout', { method: 'POST', credentials: 'same-origin' });
    location.reload();
  });

  // --- 集計表示 ---
  function renderStats(s) {
    el('rate').textContent = s.rate === null || s.rate === undefined ? '—' : (s.rate * 100).toFixed(1) + '%';
    el('total').textContent = `${s.total} / ${s.target}`;
    el('sessionName').textContent = s.session
      ? `セッション: ${s.session.name} (${statusLabel(s.session.status)})`
      : 'セッション未設定';
    el('preSession').textContent = String(s.preSessionTotal);
    if (s.webhookPending !== undefined) {
      el('webhookPending').textContent = `未送信: ${s.webhookPending} 件`;
    }
  }

  // --- セッション ---
  async function loadSessions() {
    const sessions = await api('/sessions');
    const tbody = el('sessionTable').querySelector('tbody');
    tbody.innerHTML = '';
    const filter = el('scanSessionFilter');
    const current = filter.value;
    filter.innerHTML = '<option value="">すべてのセッション</option><option value="0">(セッション未割当)</option>';
    for (const s of sessions) {
      const tr = document.createElement('tr');
      tr.innerHTML =
        `<td>${s.id}</td>` +
        `<td>${escapeHtml(s.name)}</td>` +
        `<td><span class="badge ${s.status}">${statusLabel(s.status)}</span></td>` +
        `<td class="num">${s.target_count}</td>` +
        `<td class="num">${s.count}</td>` +
        `<td class="muted">${fmtTime(s.started_at)}</td>` +
        `<td class="muted">${fmtTime(s.ended_at)}</td>`;
      const actions = document.createElement('td');
      actions.className = 'row-actions';
      actions.innerHTML =
        (s.status !== 'open'
          ? `<button class="small primary" data-open="${s.id}">開始</button>`
          : `<button class="small" data-close="${s.id}">終了</button>`) +
        `<button class="small" data-csv="${s.id}" title="生ログCSVダウンロード">CSV</button>` +
        `<button class="small" data-edit="${s.id}">編集</button>` +
        `<button class="small danger" data-del="${s.id}">削除</button>`;
      tr.appendChild(actions);
      tbody.appendChild(tr);

      const opt = document.createElement('option');
      opt.value = String(s.id);
      opt.textContent = `${s.name} (${statusLabel(s.status)})`;
      filter.appendChild(opt);
    }
    filter.value = current;

    tbody.querySelectorAll('[data-open]').forEach((b) =>
      b.addEventListener('click', async () => {
        await api(`/sessions/${b.dataset.open}/open`, { method: 'POST' });
        await refreshAll();
      })
    );
    tbody.querySelectorAll('[data-close]').forEach((b) =>
      b.addEventListener('click', async () => {
        await api(`/sessions/${b.dataset.close}/close`, { method: 'POST' });
        await refreshAll();
      })
    );
    tbody.querySelectorAll('[data-csv]').forEach((b) =>
      b.addEventListener('click', () => {
        window.location.href = `/admin/api/sessions/${b.dataset.csv}/csv`;
      })
    );
    tbody.querySelectorAll('[data-edit]').forEach((b) =>
      b.addEventListener('click', async () => {
        const s = sessions.find((x) => String(x.id) === b.dataset.edit);
        const name = prompt('セッション名', s.name);
        if (name === null) return;
        const target = prompt('点呼対象者数', String(s.target_count));
        if (target === null) return;
        await api(`/sessions/${s.id}`, { method: 'PATCH', body: JSON.stringify({ name, targetCount: Number(target) }) });
        await refreshAll();
      })
    );
    tbody.querySelectorAll('[data-del]').forEach((b) =>
      b.addEventListener('click', async () => {
        if (!confirm('このセッションを削除しますか？')) return;
        try {
          await api(`/sessions/${b.dataset.del}`, { method: 'DELETE' });
          await refreshAll();
        } catch (err) {
          alert(err.message);
        }
      })
    );
    return sessions;
  }

  el('sessionForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const name = el('sessionNameInput').value.trim();
    const targetCount = Number(el('sessionTargetInput').value) || 0;
    if (!name) return;
    await api('/sessions', { method: 'POST', body: JSON.stringify({ name, targetCount }) });
    el('sessionNameInput').value = '';
    el('sessionTargetInput').value = '';
    await refreshAll();
  });

  // --- 点呼場所 ---
  async function loadLocations() {
    const locations = await api('/locations');
    const tbody = el('locationTable').querySelector('tbody');
    tbody.innerHTML = '';
    for (const loc of locations) {
      const tr = document.createElement('tr');
      tr.innerHTML =
        `<td>${loc.id}</td>` +
        `<td>${escapeHtml(loc.name)}</td>` +
        `<td class="num">${loc.sort}</td>` +
        `<td>${loc.active ? '<span class="badge open">有効</span>' : '<span class="badge closed">無効</span>'}</td>`;
      const actions = document.createElement('td');
      actions.className = 'row-actions';
      actions.innerHTML =
        `<button class="small" data-edit="${loc.id}">編集</button>` +
        (loc.active
          ? `<button class="small" data-deact="${loc.id}">無効化</button>`
          : `<button class="small" data-act="${loc.id}">有効化</button>`) +
        `<button class="small danger" data-del="${loc.id}">削除</button>`;
      tr.appendChild(actions);
      tbody.appendChild(tr);
    }
    tbody.querySelectorAll('[data-edit]').forEach((b) =>
      b.addEventListener('click', async () => {
        const loc = locations.find((x) => String(x.id) === b.dataset.edit);
        const name = prompt('点呼場所名', loc.name);
        if (name === null) return;
        const sort = prompt('並び順', String(loc.sort));
        if (sort === null) return;
        await api(`/locations/${loc.id}`, { method: 'PATCH', body: JSON.stringify({ name, sort: Number(sort) }) });
        await refreshAll();
      })
    );
    tbody.querySelectorAll('[data-deact]').forEach((b) =>
      b.addEventListener('click', async () => {
        await api(`/locations/${b.dataset.deact}`, { method: 'PATCH', body: JSON.stringify({ active: false }) });
        await refreshAll();
      })
    );
    tbody.querySelectorAll('[data-act]').forEach((b) =>
      b.addEventListener('click', async () => {
        await api(`/locations/${b.dataset.act}`, { method: 'PATCH', body: JSON.stringify({ active: true }) });
        await refreshAll();
      })
    );
    tbody.querySelectorAll('[data-del]').forEach((b) =>
      b.addEventListener('click', async () => {
        if (!confirm('この点呼場所を削除しますか？ (受付データがある場合は無効化されます)')) return;
        await api(`/locations/${b.dataset.del}`, { method: 'DELETE' });
        await refreshAll();
      })
    );
  }

  el('locationForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const name = el('locationNameInput').value.trim();
    const sort = Number(el('locationSortInput').value) || 0;
    if (!name) return;
    await api('/locations', { method: 'POST', body: JSON.stringify({ name, sort }) });
    el('locationNameInput').value = '';
    el('locationSortInput').value = '';
    await refreshAll();
  });

  // --- 受付データ ---
  const SCAN_PAGE_SIZE = 100;
  let scanOffset = 0;
  let scanTotal = 0;
  let scanCurrentCount = 0;

  function appendScanRow(tbody, r) {
    const tr = document.createElement('tr');
    if (r.deleted) tr.className = 'muted';
    tr.innerHTML =
      `<td>${r.id}</td>` +
      `<td class="num">${r.student_number}</td>` +
      `<td>${escapeHtml(r.location_name || '場所未選択')}</td>` +
      `<td>${escapeHtml(r.session_name || '(未割当)')}</td>` +
      `<td class="muted">${fmtTime(r.received_at)}</td>` +
      `<td>${r.deleted ? `<span class="badge deleted">取消 ${fmtTime(r.deleted_at)}</span>` : '<span class="badge open">有効</span>'}</td>`;
    tbody.appendChild(tr);
  }

  async function loadScans(append = false) {
    if (!append) {
      scanOffset = 0;
      scanCurrentCount = 0;
    }
    const sessionId = el('scanSessionFilter').value;
    const includeDeleted = el('scanIncludeDeleted').checked ? '1' : '0';
    const q = el('scanSearchInput').value.trim();

    const params = new URLSearchParams({
      limit: String(SCAN_PAGE_SIZE),
      offset: String(scanOffset),
      includeDeleted
    });
    if (sessionId !== '') params.set('sessionId', sessionId);
    if (q) params.set('q', q);

    const res = await api('/scans?' + params.toString());
    const rows = Array.isArray(res) ? res : (res.rows || []);
    scanTotal = res.total !== undefined ? res.total : rows.length;

    const tbody = el('scanTable').querySelector('tbody');
    if (!append) {
      tbody.innerHTML = '';
      if (rows.length === 0) {
        tbody.innerHTML = '<tr><td colspan="6" class="muted" style="text-align:center; padding:16px;">該当するデータはありません</td></tr>';
      }
    }

    for (const r of rows) {
      appendScanRow(tbody, r);
    }
    scanCurrentCount += rows.length;

    el('scanCountInfo').textContent = `表示中: ${scanCurrentCount} / ${scanTotal} 件`;
    const loadMoreBtn = el('scanLoadMore');
    if (scanCurrentCount >= scanTotal) {
      loadMoreBtn.style.display = 'none';
    } else {
      loadMoreBtn.style.display = '';
      loadMoreBtn.textContent = `さらに読み込む (次の${Math.min(SCAN_PAGE_SIZE, scanTotal - scanCurrentCount)}件)`;
    }
  }

  el('scanFilter').addEventListener('submit', async (e) => {
    e.preventDefault();
    await loadScans(false);
  });

  el('scanResetBtn').addEventListener('click', async () => {
    el('scanSearchInput').value = '';
    el('scanSessionFilter').value = '';
    el('scanIncludeDeleted').checked = false;
    await loadScans(false);
  });

  el('scanLoadMore').addEventListener('click', async () => {
    scanOffset = scanCurrentCount;
    await loadScans(true);
  });

  // --- Power Automate (Webhook) ---
  async function loadWebhookLogs() {
    try {
      const logs = await api('/webhook/logs?limit=50');
      const tbody = el('webhookLogTable').querySelector('tbody');
      tbody.innerHTML = '';
      if (!logs || logs.length === 0) {
        tbody.innerHTML = '<tr><td colspan="6" class="muted" style="text-align:center; padding:12px;">履歴はありません</td></tr>';
        return;
      }
      for (const log of logs) {
        const tr = document.createElement('tr');
        const isSuccess = log.status === 'success';
        const statusBadge = isSuccess
          ? `<span class="badge open">成功 ${log.http_status ? '(' + log.http_status + ')' : ''}</span>`
          : `<span class="badge deleted">失敗 ${log.http_status ? '(' + log.http_status + ')' : ''}</span>`;
        const triggerLabel = log.trigger_type === 'manual' ? '手動' : '自動';
        const detail = isSuccess
          ? escapeHtml(log.student_numbers || '-')
          : `<span style="color:var(--bad)">${escapeHtml(log.error_message || '不明なエラー')}</span>${log.student_numbers ? ` (対象: ${escapeHtml(log.student_numbers)})` : ''}`;

        tr.innerHTML =
          `<td class="muted">${fmtTime(log.sent_at)}</td>` +
          `<td>${triggerLabel}</td>` +
          `<td>${statusBadge}</td>` +
          `<td class="num">${log.record_count}</td>` +
          `<td class="num">${log.response_ms != null ? log.response_ms + 'ms' : '—'}</td>` +
          `<td style="word-break: break-all; font-size: 12px;">${detail}</td>`;
        tbody.appendChild(tr);
      }
    } catch {
      // ログ読み込み失敗時は無視
    }
  }

  async function loadWebhook() {
    const w = await api('/webhook');
    el('webhookEnabled').checked = w.enabled;
    el('webhookUrlState').textContent = w.urlConfigured
      ? 'Webhook URL: 設定済み'
      : 'Webhook URL: 未設定 (.env の WEBHOOK_URL)';
    el('webhookPending').textContent = `未送信: ${w.pending} 件`;
    el('webhookInfo').textContent =
      `最終送信: ${w.lastSentAt ? fmtTime(w.lastSentAt) : '—'}` +
      (w.lastError ? ` / エラー: ${w.lastError}` : '') +
      ` (${w.batchSize}件または${Math.round(w.idleMs / 1000)}秒経過でまとめ送信)`;
    await loadWebhookLogs();
  }

  el('webhookEnabled').addEventListener('change', async () => {
    await api('/webhook', { method: 'POST', body: JSON.stringify({ enabled: el('webhookEnabled').checked }) });
    await loadWebhook();
  });

  el('webhookFlush').addEventListener('click', async () => {
    const result = await api('/webhook/flush', { method: 'POST' });
    alert(`送信しました: ${result.sent} 件`);
    await loadWebhook();
  });

  el('webhookLogsRefresh').addEventListener('click', async () => {
    await loadWebhookLogs();
  });

  async function refreshAll() {
    const [summary] = await Promise.all([api('/summary')]);
    renderStats(summary);
    await loadSessions();
    await loadLocations();
    await loadScans();
    await loadWebhook();
  }

  // --- リアルタイム ---
  let es = null;
  function connectStream() {
    if (es) return;
    es = new EventSource('/admin/api/stream');
    es.addEventListener('stats', (e) => renderStats(JSON.parse(e.data)));
    es.addEventListener('session', (e) => {
      renderStats(JSON.parse(e.data));
      loadSessions().catch(() => {});
    });
    es.addEventListener('scan', () => {
      if (scanOffset === 0 && !el('scanSearchInput').value.trim()) {
        loadScans(false).catch(() => {});
      }
    });
    es.addEventListener('cancel', () => {
      if (scanOffset === 0 && !el('scanSearchInput').value.trim()) {
        loadScans(false).catch(() => {});
      }
    });
    es.onopen = () => {
      el('conn').textContent = '接続中';
      el('conn').className = 'conn ok';
    };
    es.onerror = () => {
      el('conn').textContent = '切断 (再接続中...)';
      el('conn').className = 'conn bad';
    };
  }

  // --- 起動 ---
  (async function init() {
    try {
      await api('/me');
      hideLogin();
      await refreshAll();
      connectStream();
    } catch {
      showLogin();
    }
  })();
})();
