'use strict';

const config = require('./config');
const { db, nowIso, getOpenSession } = require('./db');

// 受付したスキャンを Webhook (Power Automate) へ転送する。
// INTERVAL_MS ごとに、未送信の受付を 1 つの Webhook にまとめて送る。
// ペイロードは { records: [{ student_number, location, scan_time }] } の最小 JSON。
// Power Automate 側でメール本文を組み立て、BCC で一括送信する想定。
// 自動送信は管理画面から停止でき、その場合も未送信分は保持され、手動送信で送れる。

const lastResult = { lastSentAt: null, lastError: null };
let sending = false;
let retryAt = 0;

// 1 回の自動送信で扱う上限。超過分は次回のインターバルで送る。
const AUTO_SEND_LIMIT = 10000;

function isEnabled() {
  const row = db.prepare("SELECT value FROM settings WHERE key = 'webhook_enabled'").get();
  if (!row) {
    db.prepare("INSERT INTO settings (key, value) VALUES ('webhook_enabled', '1')").run();
    return true;
  }
  return row.value === '1';
}

function setEnabled(enabled) {
  db.prepare(
    "INSERT INTO settings (key, value) VALUES ('webhook_enabled', ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value"
  ).run(enabled ? '1' : '0');
}

// 自動送信の対象は現在オープン中のセッションの受付のみ。
// セッション外(session_id が NULL)と過去セッションの未送信は、管理画面の選択送信でのみ送る。
function pendingCount() {
  const session = getOpenSession();
  if (!session) return 0;
  return db
    .prepare('SELECT COUNT(*) AS c FROM attendance WHERE webhook_sent = 0 AND deleted = 0 AND session_id = ?')
    .get(session.id).c;
}

function getUnsent(limit) {
  const session = getOpenSession();
  if (!session) return [];
  return db
    .prepare(
      'SELECT * FROM attendance WHERE webhook_sent = 0 AND deleted = 0 AND session_id = ? ORDER BY id LIMIT ?'
    )
    .all(session.id, limit);
}

function markSent(ids) {
  const stmt = db.prepare('UPDATE attendance SET webhook_sent = 1 WHERE id = ?');
  const tx = db.transaction((list) => {
    for (const id of list) stmt.run(id);
  });
  tx(ids);
}

function locationNameMap() {
  const map = new Map();
  for (const row of db.prepare('SELECT id, name FROM locations').all()) {
    map.set(row.id, row.name);
  }
  return map;
}

async function sendBatch(records) {
  const names = locationNameMap();

  // Power Automate (Teams Webhook トリガー) 向けの最小 JSON。
  // scan_time はクライアントが送信した client_time (ISO 8601) をそのまま使う。
  const payload = {
    records: records.map((r) => ({
      student_number: r.student_number,
      location: (r.location_id != null ? names.get(r.location_id) : null) || '場所未選択',
      scan_time: r.client_time || ''
    }))
  };

  const headers = { 'Content-Type': 'application/json' };
  if (config.webhookSecret) headers['X-Webhook-Secret'] = config.webhookSecret;

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 10000);
  const startMs = Date.now();
  try {
    const resp = await fetch(config.webhookUrl, {
      method: 'POST',
      headers,
      body: JSON.stringify(payload),
      signal: controller.signal
    });
    const responseMs = Date.now() - startMs;
    if (!resp.ok) {
      const err = new Error(`HTTP ${resp.status}`);
      err.httpStatus = resp.status;
      err.responseMs = responseMs;
      throw err;
    }
    return { httpStatus: resp.status, responseMs };
  } catch (err) {
    if (!err.responseMs) {
      err.responseMs = Date.now() - startMs;
    }
    throw err;
  } finally {
    clearTimeout(timer);
  }
}

function recordLog({ triggerType, recordCount, status, httpStatus, responseMs, errorMessage, studentNumbers }) {
  try {
    db.prepare(`
      INSERT INTO webhook_logs (sent_at, trigger_type, record_count, status, http_status, response_ms, error_message, student_numbers)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?)
    `).run(
      nowIso(),
      triggerType,
      recordCount,
      status,
      httpStatus !== undefined ? httpStatus : null,
      responseMs !== undefined ? responseMs : null,
      errorMessage || null,
      studentNumbers || null
    );
  } catch (err) {
    console.error('Failed to insert webhook_log:', err);
  }
}

function getLogs(limit = 50) {
  const safeLimit = Math.min(Math.max(Number(limit) || 50, 1), 500);
  return db.prepare('SELECT * FROM webhook_logs ORDER BY id DESC LIMIT ?').all(safeLimit);
}

// 1 バッチ(最大 BATCH_SIZE 件)を 1 Webhook として送る
// records を渡すとそのレコードを送信対象にする (手動の選択送信用)。未指定なら未送信を取得する。
async function sendOneBatch(triggerType = 'auto', records = null) {
  if (!config.webhookUrl) return 0;
  if (!records) records = getUnsent(config.webhookBatchSize);
  if (records.length === 0) return 0;

  const studentNumbers = records.map((r) => r.student_number).join(', ');
  try {
    const { httpStatus, responseMs } = await sendBatch(records);
    markSent(records.map((r) => r.id));
    lastResult.lastSentAt = nowIso();
    lastResult.lastError = null;

    recordLog({
      triggerType,
      recordCount: records.length,
      status: 'success',
      httpStatus,
      responseMs,
      errorMessage: null,
      studentNumbers
    });

    return records.length;
  } catch (err) {
    retryAt = Date.now() + 5000;
    const msg = String(err && err.message ? err.message : err);
    lastResult.lastError = msg;

    recordLog({
      triggerType,
      recordCount: records.length,
      status: 'failure',
      httpStatus: err.httpStatus || null,
      responseMs: err.responseMs || null,
      errorMessage: msg,
      studentNumbers
    });

    return 0;
  }
}

// 現在のセッションの未送信をすべて送る (手動送信用)
async function flushAll(maxBatches = 200) {
  if (!config.webhookUrl) return { sent: 0, reason: 'no_url' };
  if (sending) return { sent: 0, reason: 'busy' };

  sending = true;
  let sent = 0;
  try {
    for (let i = 0; i < maxBatches; i++) {
      const count = await sendOneBatch('manual');
      if (count === 0) break;
      sent += count;
      if (count < config.webhookBatchSize) break;
    }
  } finally {
    sending = false;
  }
  return { sent };
}

// 選択した受付データを送信する (手動送信用)。取消済みは対象外。送信済みも再送できる。
async function sendSelected(ids) {
  if (!config.webhookUrl) return { sent: 0, reason: 'no_url' };
  if (sending) return { sent: 0, reason: 'busy' };

  const list = Array.from(
    new Set((Array.isArray(ids) ? ids : []).map(Number).filter((n) => Number.isInteger(n) && n > 0))
  ).slice(0, 10000);
  if (list.length === 0) return { sent: 0, reason: 'no_records' };

  const placeholders = list.map(() => '?').join(',');
  const records = db
    .prepare(`SELECT * FROM attendance WHERE deleted = 0 AND id IN (${placeholders}) ORDER BY id`)
    .all(...list);
  if (records.length === 0) return { sent: 0, reason: 'no_records' };

  sending = true;
  let sent = 0;
  try {
    for (let i = 0; i < records.length; i += config.webhookBatchSize) {
      const chunk = records.slice(i, i + config.webhookBatchSize);
      const count = await sendOneBatch('manual', chunk);
      sent += count;
      if (count === 0) break;
    }
  } finally {
    sending = false;
  }
  return { sent, requested: records.length };
}

// 定期送信: 未送信を 1 つの Webhook にまとめて送る
async function tick() {
  if (!config.webhookUrl) return;
  if (!isEnabled()) return;
  if (sending || Date.now() < retryAt) return;
  if (pendingCount() === 0) return;

  sending = true;
  try {
    await sendOneBatch('auto', getUnsent(AUTO_SEND_LIMIT));
  } finally {
    sending = false;
  }
}

function status() {
  return {
    enabled: isEnabled(),
    urlConfigured: Boolean(config.webhookUrl),
    pending: pendingCount(),
    batchSize: config.webhookBatchSize,
    intervalMs: config.webhookIntervalMs,
    lastSentAt: lastResult.lastSentAt,
    lastError: lastResult.lastError
  };
}

function start() {
  console.log(`[info] Webhook 自動送信間隔: ${Math.round(config.webhookIntervalMs / 1000)} 秒`);
  setInterval(() => tick().catch(() => {}), config.webhookIntervalMs).unref();
}

module.exports = { start, isEnabled, setEnabled, pendingCount, flushAll, status, tick, getLogs, recordLog, sendSelected };
