'use strict';

(function () {
  const params = new URLSearchParams(location.search);
  if (params.get('token')) localStorage.setItem('tenko_token', params.get('token'));
  const token = localStorage.getItem('tenko_token') || '';
  const qs = token ? `?token=${encodeURIComponent(token)}` : '';

  const el = (id) => document.getElementById(id);
  const feed = el('feed');
  const feedItems = [];
  const MAX_FEED = 100;

  function fmtRate(rate) {
    if (rate === null || rate === undefined) return '—';
    return (rate * 100).toFixed(1) + '%';
  }

  function fmtTime(iso) {
    if (!iso) return '';
    return new Date(iso).toLocaleTimeString('ja-JP');
  }

  function statusLabel(status) {
    return status === 'open' ? '実施中' : status === 'closed' ? '終了' : '待機';
  }

  function escapeHtml(value) {
    return String(value === null || value === undefined ? '' : value).replace(/[&<>"']/g, (c) => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    }[c]));
  }

  function renderStats(s) {
    el('rate').textContent = fmtRate(s.rate);
    el('total').textContent = `${s.total} / ${s.target}`;
    el('sessionName').textContent = s.session
      ? `セッション: ${s.session.name} (${statusLabel(s.session.status)})`
      : 'セッション未設定';
    el('serverTime').textContent = s.serverTime ? '更新: ' + fmtTime(s.serverTime) : '';
    el('preSession').textContent = String(s.preSessionTotal);

    const tbody = el('locTable').querySelector('tbody');
    tbody.innerHTML = '';
    for (const loc of s.byLocation) {
      const tr = document.createElement('tr');
      tr.innerHTML = `<td>${escapeHtml(loc.name)}</td><td class="num">${loc.count}</td>`;
      tbody.appendChild(tr);
    }
    if (s.unassignedLocation > 0) {
      const tr = document.createElement('tr');
      tr.innerHTML = `<td class="muted">(場所未選択)</td><td class="num">${s.unassignedLocation}</td>`;
      tbody.appendChild(tr);
    }
  }

  function renderFeed() {
    feed.innerHTML = '';
    for (const it of feedItems) {
      const li = document.createElement('li');
      if (it.cancelled) li.className = 'cancelled';
      li.innerHTML =
        `<span class="loc">${escapeHtml(it.locationName || '場所未選択')}</span>` +
        `<span class="time">${fmtTime(it.receivedAt)}</span>` +
        (it.cancelled ? '<span class="tag">取消</span>' : '');
      feed.appendChild(li);
    }
    el('feedCount').textContent = `(${feedItems.length}件)`;
  }

  function addScan(ev) {
    feedItems.unshift(ev);
    if (feedItems.length > MAX_FEED) feedItems.pop();
    renderFeed();
  }

  function markCancel(ev) {
    const item = feedItems.find((x) => x.clientScanId === ev.clientScanId);
    if (item) item.cancelled = true;
    renderFeed();
  }

  const conn = el('conn');

  function connect() {
    const es = new EventSource('/api/stream' + qs);
    es.addEventListener('stats', (e) => renderStats(JSON.parse(e.data)));
    es.addEventListener('session', (e) => renderStats(JSON.parse(e.data)));
    es.addEventListener('scan', (e) => addScan(JSON.parse(e.data)));
    es.addEventListener('cancel', (e) => markCancel(JSON.parse(e.data)));
    es.onopen = () => {
      conn.textContent = '接続中';
      conn.className = 'conn ok';
    };
    es.onerror = () => {
      conn.textContent = '切断 (再接続中...)';
      conn.className = 'conn bad';
    };
  }

  fetch('/api/summary' + qs)
    .then((r) => r.json())
    .then(renderStats)
    .catch(() => {});

  connect();
})();
