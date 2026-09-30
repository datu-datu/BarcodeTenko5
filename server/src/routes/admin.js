'use strict';

const express = require('express');
const router = express.Router();

const { db, nowIso } = require('../db');
const config = require('../config');
const auth = require('../auth');
const events = require('../events');
const webhook = require('../webhook');
const { computeSummary } = require('../summary');

function broadcastStats() {
  events.broadcast('stats', computeSummary());
}
function broadcastSession() {
  events.broadcast('session', computeSummary());
}

// --- 認証 ---
router.post('/login', async (req, res) => {
  if (!config.adminPassword) return res.status(503).json({ error: 'admin password is not configured' });
  if (!config.sessionSecret) return res.status(503).json({ error: 'session secret is not configured' });

  const ip = req.ip || req.socket.remoteAddress || 'unknown';
  const rate = auth.checkLoginRateLimit(ip);
  if (rate.locked) {
    const minutes = Math.ceil(rate.retryAfterSeconds / 60);
    res.setHeader('Retry-After', String(rate.retryAfterSeconds));
    return res.status(429).json({
      error: `ログイン試行回数が上限を超えました。約${minutes}分後に再試行してください。`,
      retryAfter: rate.retryAfterSeconds
    });
  }

  const password = (req.body || {}).password;
  if (typeof password !== 'string' || !auth.safeEqual(password, config.adminPassword)) {
    await auth.recordLoginFailure(ip);
    return res.status(401).json({ error: 'パスワードが正しくありません' });
  }

  auth.recordLoginSuccess(ip);
  const secure = req.secure ? '; Secure' : '';
  res.setHeader(
    'Set-Cookie',
    `${auth.SESSION_COOKIE}=${auth.makeSessionCookie()}; HttpOnly; Path=/; SameSite=Lax${secure}; Max-Age=${auth.SESSION_TTL_MS / 1000}`
  );
  res.json({ ok: true });
});

router.post('/logout', (req, res) => {
  res.setHeader('Set-Cookie', `${auth.SESSION_COOKIE}=; HttpOnly; Path=/; SameSite=Lax; Max-Age=0`);
  res.json({ ok: true });
});

router.get('/me', auth.adminAuth, (req, res) => res.json({ ok: true }));

// これ以降は管理者認証必須
router.use(auth.adminAuth);

router.get('/summary', (req, res) => res.json(computeSummary()));

// --- Power Automate (Webhook) ---
router.get('/webhook', (req, res) => {
  res.json(webhook.status());
});

router.post('/webhook', (req, res) => {
  const enabled = Boolean((req.body || {}).enabled);
  webhook.setEnabled(enabled);
  broadcastStats();
  res.json(webhook.status());
});

router.post('/webhook/flush', async (req, res) => {
  const result = await webhook.flushAll();
  broadcastStats();
  res.json({ ...result, ...webhook.status() });
});

router.post('/webhook/send', async (req, res) => {
  const ids = (req.body || {}).ids;
  const result = await webhook.sendSelected(ids);
  broadcastStats();
  res.json({ ...result, ...webhook.status() });
});

router.get('/webhook/logs', (req, res) => {
  const limit = req.query.limit ? Number(req.query.limit) : 50;
  res.json(webhook.getLogs(limit));
});


// --- 点呼場所 ---
router.get('/locations', (req, res) => {
  res.json(db.prepare('SELECT * FROM locations ORDER BY sort, id').all());
});

router.post('/locations', (req, res) => {
  const body = req.body || {};
  const name = typeof body.name === 'string' ? body.name.trim() : '';
  const sort = Number(body.sort) || 0;
  if (!name) return res.status(400).json({ error: 'name is required' });
  const info = db.prepare('INSERT INTO locations (name, sort, active) VALUES (?, ?, 1)').run(name, sort);
  res.json(db.prepare('SELECT * FROM locations WHERE id = ?').get(info.lastInsertRowid));
});

router.patch('/locations/:id', (req, res) => {
  const id = Number(req.params.id);
  const loc = db.prepare('SELECT * FROM locations WHERE id = ?').get(id);
  if (!loc) return res.status(404).json({ error: 'not found' });
  const body = req.body || {};
  const name = body.name !== undefined ? String(body.name).trim() : loc.name;
  const sort = body.sort !== undefined ? Number(body.sort) || 0 : loc.sort;
  const active = body.active !== undefined ? (body.active ? 1 : 0) : loc.active;
  db.prepare('UPDATE locations SET name = ?, sort = ?, active = ? WHERE id = ?').run(name, sort, active, id);
  res.json(db.prepare('SELECT * FROM locations WHERE id = ?').get(id));
});

router.delete('/locations/:id', (req, res) => {
  const id = Number(req.params.id);
  const used = db.prepare('SELECT COUNT(*) AS c FROM attendance WHERE location_id = ?').get(id).c;
  if (used > 0) {
    db.prepare('UPDATE locations SET active = 0 WHERE id = ?').run(id);
    return res.json({ ok: true, deactivated: true });
  }
  db.prepare('DELETE FROM locations WHERE id = ?').run(id);
  res.json({ ok: true, deleted: true });
});

// --- セッション ---
router.get('/sessions', (req, res) => {
  const rows = db.prepare('SELECT * FROM sessions ORDER BY id DESC').all();
  const countScan = db.prepare('SELECT COUNT(*) AS c FROM attendance WHERE session_id = ? AND deleted = 0');
  const countClients = db.prepare(
    'SELECT COUNT(DISTINCT client_id) AS c FROM attendance WHERE session_id = ? AND client_id IS NOT NULL'
  );
  const countCompleted = db.prepare('SELECT COUNT(*) AS c FROM session_completions WHERE session_id = ?');
  res.json(
    rows.map((s) => ({
      ...s,
      count: countScan.get(s.id).c,
      clientTotal: countClients.get(s.id).c,
      clientCompleted: countCompleted.get(s.id).c
    }))
  );
});

// セッションごとの参加クライアントと完了状況 (終了時の警告ダイアログ用)
router.get('/sessions/:id/completions', (req, res) => {
  const id = Number(req.params.id);
  const session = db.prepare('SELECT * FROM sessions WHERE id = ?').get(id);
  if (!session) return res.status(404).json({ error: 'not found' });

  // 参加クライアント = このセッションでスキャンが1件でも届いた client_id (取消済みも含む)
  const participants = db
    .prepare(
      `SELECT a.client_id AS clientId,
              GROUP_CONCAT(DISTINCT COALESCE(l.name, '場所未選択')) AS locationNames
         FROM attendance a
         LEFT JOIN locations l ON l.id = a.location_id
        WHERE a.session_id = ? AND a.client_id IS NOT NULL
        GROUP BY a.client_id`
    )
    .all(id);
  const completions = db
    .prepare('SELECT client_id, scan_count, bin_name, completed_at FROM session_completions WHERE session_id = ?')
    .all(id);
  const byClient = new Map(completions.map((c) => [c.client_id, c]));

  const clients = participants.map((p) => {
    const done = byClient.get(p.clientId);
    return {
      clientId: p.clientId,
      locations: p.locationNames ? p.locationNames.split(',') : [],
      completed: Boolean(done),
      completedAt: done ? done.completed_at : null,
      scanCount: done ? done.scan_count : null,
      binName: done ? done.bin_name : null
    };
  });

  res.json({
    sessionId: id,
    clientTotal: clients.length,
    clientCompleted: clients.filter((c) => c.completed).length,
    clients
  });
});

router.post('/sessions', (req, res) => {
  const body = req.body || {};
  const name = typeof body.name === 'string' ? body.name.trim() : '';
  const targetCount = Number(body.targetCount) || 0;
  if (!name) return res.status(400).json({ error: 'name is required' });
  const info = db
    .prepare('INSERT INTO sessions (name, status, target_count, created_at) VALUES (?, ?, ?, ?)')
    .run(name, 'idle', targetCount, nowIso());
  broadcastSession();
  res.json(db.prepare('SELECT * FROM sessions WHERE id = ?').get(info.lastInsertRowid));
});

router.patch('/sessions/:id', (req, res) => {
  const id = Number(req.params.id);
  const s = db.prepare('SELECT * FROM sessions WHERE id = ?').get(id);
  if (!s) return res.status(404).json({ error: 'not found' });
  const body = req.body || {};
  const name = body.name !== undefined ? String(body.name).trim() : s.name;
  const targetCount = body.targetCount !== undefined ? Number(body.targetCount) || 0 : s.target_count;
  db.prepare('UPDATE sessions SET name = ?, target_count = ? WHERE id = ?').run(name, targetCount, id);
  broadcastStats();
  res.json(db.prepare('SELECT * FROM sessions WHERE id = ?').get(id));
});

router.post('/sessions/:id/open', (req, res) => {
  const id = Number(req.params.id);
  const s = db.prepare('SELECT * FROM sessions WHERE id = ?').get(id);
  if (!s) return res.status(404).json({ error: 'not found' });
  // 同時に開けるセッションは1つだけ
  db.prepare("UPDATE sessions SET status = 'closed', ended_at = ? WHERE status = 'open' AND id <> ?").run(nowIso(), id);
  db.prepare("UPDATE sessions SET status = 'open', started_at = COALESCE(started_at, ?), ended_at = NULL WHERE id = ?").run(
    nowIso(),
    id
  );
  broadcastSession();
  res.json(db.prepare('SELECT * FROM sessions WHERE id = ?').get(id));
});

router.post('/sessions/:id/close', (req, res) => {
  const id = Number(req.params.id);
  const s = db.prepare('SELECT * FROM sessions WHERE id = ?').get(id);
  if (!s) return res.status(404).json({ error: 'not found' });
  db.prepare("UPDATE sessions SET status = 'closed', ended_at = ? WHERE id = ?").run(nowIso(), id);
  broadcastSession();
  res.json(db.prepare('SELECT * FROM sessions WHERE id = ?').get(id));
});

router.delete('/sessions/:id', (req, res) => {
  const id = Number(req.params.id);
  const used = db.prepare('SELECT COUNT(*) AS c FROM attendance WHERE session_id = ?').get(id).c;
  if (used > 0) return res.status(409).json({ error: 'session has attendance records' });
  db.prepare('DELETE FROM sessions WHERE id = ?').run(id);
  broadcastSession();
  res.json({ ok: true });
});

router.get('/sessions/:id/csv', (req, res) => {
  const id = Number(req.params.id);
  const session = db.prepare('SELECT * FROM sessions WHERE id = ?').get(id);
  if (!session) return res.status(404).send('Session not found');

  const rows = db
    .prepare(
      `SELECT a.id, a.student_number, l.name AS location_name, s.name AS session_name,
              a.client_time, a.received_at, a.deleted, a.deleted_at
         FROM attendance a
         LEFT JOIN locations l ON l.id = a.location_id
         LEFT JOIN sessions  s ON s.id = a.session_id
        WHERE a.session_id = ?
        ORDER BY a.id ASC`
    )
    .all(id);

  function formatJst(iso) {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      const jst = new Date(d.getTime() + 9 * 60 * 60 * 1000);
      const pad = (n) => String(n).padStart(2, '0');
      const y = jst.getUTCFullYear();
      const m = pad(jst.getUTCMonth() + 1);
      const day = pad(jst.getUTCDate());
      const h = pad(jst.getUTCHours());
      const min = pad(jst.getUTCMinutes());
      const sec = pad(jst.getUTCSeconds());
      return `${y}-${m}-${day} ${h}:${min}:${sec}`;
    } catch {
      return iso;
    }
  }

  function csvEscape(val) {
    if (val === null || val === undefined) return '""';
    const str = String(val);
    return `"${str.replace(/"/g, '""')}"`;
  }

  const header = ['ID', '学籍番号', '点呼場所', 'セッション名', 'スキャン時刻', '受付日時', '状態', '取消日時'];
  const lines = [header.map(csvEscape).join(',')];

  for (const r of rows) {
    lines.push([
      r.id,
      r.student_number,
      csvEscape(r.location_name || '場所未選択'),
      csvEscape(r.session_name || session.name),
      csvEscape(formatJst(r.client_time)),
      csvEscape(formatJst(r.received_at)),
      csvEscape(r.deleted ? '取消' : '有効'),
      csvEscape(formatJst(r.deleted_at))
    ].join(','));
  }

  const bom = '\uFEFF';
  const csvContent = bom + lines.join('\r\n') + '\r\n';

  const safeName = session.name.replace(/[/\\?%*:|"<>]/g, '_');
  const filename = `session_${session.id}_${safeName}.csv`;
  const encodedFilename = encodeURIComponent(filename);

  res.setHeader('Content-Type', 'text/csv; charset=utf-8');
  res.setHeader('Content-Disposition', `attachment; filename="${encodedFilename}"; filename*=UTF-8''${encodedFilename}`);
  res.send(csvContent);
});

// --- スキャン一覧 ---
// 受付データ一覧のフィルタ条件を組み立てる (GET /scans と GET /scans/ids で共用)
function buildScanWhere(query) {
  const includeDeleted = query.includeDeleted === '1' || query.includeDeleted === 'true';
  const sessionFilter =
    query.sessionId !== undefined && query.sessionId !== '' && Number.isInteger(Number(query.sessionId))
      ? Number(query.sessionId)
      : null;
  const q = typeof query.q === 'string' ? query.q.trim() : '';

  const conditions = [];
  const params = [];
  if (!includeDeleted) conditions.push('a.deleted = 0');
  if (sessionFilter !== null) {
    if (sessionFilter === 0) {
      conditions.push('a.session_id IS NULL');
    } else {
      conditions.push('a.session_id = ?');
      params.push(sessionFilter);
    }
  }
  if (q) {
    conditions.push('CAST(a.student_number AS TEXT) LIKE ?');
    params.push(`%${q}%`);
  }
  return { where: conditions.length ? 'WHERE ' + conditions.join(' AND ') : '', params };
}

// 選択送信・取消・復元で扱う ID の上限と分割幅 (SQLite のバインド変数上限対策)
const SCAN_ID_LIMIT = 10000;
const UPDATE_CHUNK_SIZE = 500;

function normalizeIds(ids) {
  const list = (Array.isArray(ids) ? ids : [])
    .map(Number)
    .filter((n) => Number.isInteger(n) && n > 0);
  return Array.from(new Set(list)).slice(0, SCAN_ID_LIMIT);
}

function runChunkedUpdate(ids, updateChunk) {
  const tx = db.transaction((list) => {
    let changes = 0;
    for (let i = 0; i < list.length; i += UPDATE_CHUNK_SIZE) {
      changes += updateChunk(list.slice(i, i + UPDATE_CHUNK_SIZE));
    }
    return changes;
  });
  return tx(ids);
}

// フィルタ条件に一致する未送信レコードの ID 一覧 (「未送信をすべて選択」用)
router.get('/scans/ids', (req, res) => {
  const { where, params } = buildScanWhere({ ...req.query, includeDeleted: '0' });
  const fullWhere = where ? `${where} AND a.webhook_sent = 0` : 'WHERE a.webhook_sent = 0';

  const total = db.prepare(`SELECT COUNT(*) AS c FROM attendance a ${fullWhere}`).get(...params).c;
  const ids = db
    .prepare(`SELECT a.id FROM attendance a ${fullWhere} ORDER BY a.id DESC LIMIT ?`)
    .all(...params, SCAN_ID_LIMIT)
    .map((r) => r.id);

  res.json({ ids, total });
});

// 選択した受付データをまとめて取り消す (論理削除。未送信なら送信対象からも外れる)
router.post('/scans/cancel', (req, res) => {
  const ids = normalizeIds((req.body || {}).ids);
  if (ids.length === 0) return res.status(400).json({ error: 'ids is required' });

  const cancelled = runChunkedUpdate(ids, (chunk) => {
    const placeholders = chunk.map(() => '?').join(',');
    return db
      .prepare(`UPDATE attendance SET deleted = 1, deleted_at = ? WHERE deleted = 0 AND id IN (${placeholders})`)
      .run(nowIso(), ...chunk).changes;
  });

  broadcastStats();
  if (cancelled > 0) events.broadcast('cancel', { ids });
  res.json({ cancelled });
});

// 選択した取消済みデータを復元する
router.post('/scans/restore', (req, res) => {
  const ids = normalizeIds((req.body || {}).ids);
  if (ids.length === 0) return res.status(400).json({ error: 'ids is required' });

  const restored = runChunkedUpdate(ids, (chunk) => {
    const placeholders = chunk.map(() => '?').join(',');
    return db
      .prepare(`UPDATE attendance SET deleted = 0, deleted_at = NULL WHERE deleted = 1 AND id IN (${placeholders})`)
      .run(...chunk).changes;
  });

  broadcastStats();
  if (restored > 0) events.broadcast('cancel', { ids, restored: true });
  res.json({ restored });
});

router.get('/scans', (req, res) => {
  const limit = Math.min(Math.max(Number(req.query.limit) || 100, 1), 500);
  const offset = Math.max(Number(req.query.offset) || 0, 0);
  const { where, params } = buildScanWhere(req.query);

  const total = db.prepare(`SELECT COUNT(*) AS c FROM attendance a ${where}`).get(...params).c;

  const rows = db
    .prepare(
      `SELECT a.id, a.client_scan_id, a.session_id, a.location_id, a.student_number, a.received_at,
              a.client_time, a.deleted, a.deleted_at, a.webhook_sent,
              l.name AS location_name, s.name AS session_name
         FROM attendance a
         LEFT JOIN locations l ON l.id = a.location_id
         LEFT JOIN sessions  s ON s.id = a.session_id
         ${where}
         ORDER BY a.id DESC
         LIMIT ? OFFSET ?`
    )
    .all(...params, limit, offset);

  res.json({ total, limit, offset, rows });
});

// --- 管理画面用 SSE ---
router.get('/stream', (req, res) => {
  res.set({
    'Content-Type': 'text/event-stream',
    'Cache-Control': 'no-cache, no-transform',
    Connection: 'keep-alive',
    'X-Accel-Buffering': 'no'
  });
  res.flushHeaders();
  res.write('retry: 5000\n\n');
  events.addClient(res);
  res.write(`event: stats\ndata: ${JSON.stringify(computeSummary())}\n\n`);
  req.on('close', () => events.removeClient(res));
});

module.exports = router;
