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
  res.setHeader(
    'Set-Cookie',
    `${auth.SESSION_COOKIE}=${auth.makeSessionCookie()}; HttpOnly; Path=/; SameSite=Lax; Max-Age=${auth.SESSION_TTL_MS / 1000}`
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
  res.json(
    rows.map((s) => ({
      ...s,
      count: db.prepare('SELECT COUNT(*) AS c FROM attendance WHERE session_id = ? AND deleted = 0').get(s.id).c
    }))
  );
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

// --- スキャン一覧 ---
router.get('/scans', (req, res) => {
  const includeDeleted = req.query.includeDeleted === '1' || req.query.includeDeleted === 'true';
  const sessionFilter = req.query.sessionId !== undefined && req.query.sessionId !== '' ? Number(req.query.sessionId) : null;
  const limit = Math.min(Number(req.query.limit) || 200, 1000);

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
  const where = conditions.length ? 'WHERE ' + conditions.join(' AND ') : '';

  const rows = db
    .prepare(
      `SELECT a.id, a.client_scan_id, a.session_id, a.location_id, a.student_number, a.received_at,
              a.client_time, a.deleted, a.deleted_at,
              l.name AS location_name, s.name AS session_name
         FROM attendance a
         LEFT JOIN locations l ON l.id = a.location_id
         LEFT JOIN sessions  s ON s.id = a.session_id
         ${where}
         ORDER BY a.id DESC
         LIMIT ${limit}`
    )
    .all(...params);

  res.json(rows);
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
