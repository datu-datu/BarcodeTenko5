'use strict';

const config = require('./config');
const { db, nowIso } = require('./db');

// 受付したスキャンを Webhook (Power Automate の Teams Webhook) へ転送する。
// 負荷軽減のため、BATCH_SIZE 件たまるか、最後の受付から IDLE_MS 経過したら 1 つの Webhook にまとめて送る。
// 自動送信は管理画面から停止でき、その場合も未送信分は保持され、手動送信で送れる。

const lastResult = { lastSentAt: null, lastError: null };
let sending = false;
let retryAt = 0;

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

function pendingCount() {
  return db.prepare('SELECT COUNT(*) AS c FROM attendance WHERE webhook_sent = 0 AND deleted = 0').get().c;
}

function getUnsent(limit) {
  return db.prepare('SELECT * FROM attendance WHERE webhook_sent = 0 AND deleted = 0 ORDER BY id LIMIT ?').all(limit);
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

function fmtJstTime(iso) {
  if (!iso) return '';
  try {
    const d = new Date(iso);
    return d.toLocaleTimeString('ja-JP', { timeZone: 'Asia/Tokyo', hour12: false });
  } catch {
    return iso;
  }
}

async function sendBatch(records) {
  const names = locationNameMap();

  // Microsoft Teams ワークフロー (無料の Teams Webhook) が要求する Adaptive Card 形式
  const payload = {
    type: 'AdaptiveCard',
    $schema: 'http://adaptivecards.io/schemas/adaptive-card.json',
    version: '1.4',
    body: [
      {
        type: 'TextBlock',
        text: `点呼受付 (${records.length}件)`,
        weight: 'Bolder',
        size: 'Medium',
        color: 'Accent'
      },
      {
        type: 'FactSet',
        facts: records.map((r) => {
          const loc = (r.location_id != null ? names.get(r.location_id) : null) || '場所未選択';
          const timeStr = fmtJstTime(r.received_at);
          return {
            title: `学籍 ${r.student_number}`,
            value: `${loc} (${timeStr})`
          };
        })
      }
    ]
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
async function sendOneBatch(triggerType = 'auto') {
  if (!config.webhookUrl) return 0;
  const records = getUnsent(config.webhookBatchSize);
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

// 未送信をすべて送る (手動送信用)
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

// 自動送信の判定
async function tick() {
  if (!config.webhookUrl) return;
  if (!isEnabled()) return;
  if (sending || Date.now() < retryAt) return;

  const total = pendingCount();
  if (total === 0) return;

  if (total >= config.webhookBatchSize) {
    sending = true;
    try {
      await sendOneBatch('auto');
    } finally {
      sending = false;
    }
    return;
  }

  const last = db.prepare('SELECT MAX(received_at) AS m FROM attendance WHERE webhook_sent = 0 AND deleted = 0').get().m;
  if (last && Date.now() - Date.parse(last) >= config.webhookIdleMs) {
    sending = true;
    try {
      await sendOneBatch('auto');
    } finally {
      sending = false;
    }
  }
}

function status() {
  return {
    enabled: isEnabled(),
    urlConfigured: Boolean(config.webhookUrl),
    pending: pendingCount(),
    batchSize: config.webhookBatchSize,
    idleMs: config.webhookIdleMs,
    lastSentAt: lastResult.lastSentAt,
    lastError: lastResult.lastError
  };
}

function start() {
  setInterval(() => tick().catch(() => {}), 1000).unref();
}

module.exports = { start, isEnabled, setEnabled, pendingCount, flushAll, status, tick, getLogs, recordLog };
