'use strict';

const express = require('express');
const router = express.Router();

const { db, nowIso } = require('../db');
const events = require('../events');
const auth = require('../auth');
const { computeSummary } = require('../summary');
const { normalizeCode, MAX_STUDENT_NUMBER } = require('../code');

function broadcastSummary() {
  events.broadcast('stats', computeSummary());
}

function scanEvent(row, locationName) {
  return {
    id: row.id,
    clientScanId: row.client_scan_id,
    studentNumber: row.student_number,
    sessionId: row.session_id,
    locationId: row.location_id,
    locationName: locationName || null,
    receivedAt: row.received_at
  };
}

// スキャン受付。セッション未開始でも常に記録する(session_id は NULL)。
router.post('/scan', auth.clientAuth, (req, res) => {
  const body = req.body || {};
  const clientScanId = body.clientScanId;
  if (typeof clientScanId !== 'string' || clientScanId.length === 0) {
    return res.status(400).json({ error: 'clientScanId is required' });
  }

  const studentNumber = normalizeCode(body.code);
  if (studentNumber === null) {
    return res.status(400).json({ error: 'invalid code' });
  }
  if (studentNumber > MAX_STUDENT_NUMBER) {
    return res.status(400).json({ error: 'student number out of range' });
  }

  // 冪等化: 同じ client_scan_id は再送とみなし既存を返す
  const existing = db.prepare('SELECT * FROM attendance WHERE client_scan_id = ?').get(clientScanId);
  if (existing) {
    return res.json({
      ok: true,
      created: false,
      id: existing.id,
      clientScanId,
      studentNumber: existing.student_number,
      sessionId: existing.session_id,
      receivedAt: existing.received_at
    });
  }

  const openSession = db.prepare("SELECT id FROM sessions WHERE status = 'open' ORDER BY id DESC LIMIT 1").get();
  const locationId = Number.isInteger(body.locationId) ? body.locationId : null;
  const location = locationId !== null ? db.prepare('SELECT * FROM locations WHERE id = ?').get(locationId) : null;
  const receivedAt = nowIso();

  const info = db
    .prepare(
      `INSERT INTO attendance (client_scan_id, session_id, location_id, student_number, client_time, received_at, client_id, deleted)
       VALUES (?, ?, ?, ?, ?, ?, ?, 0)`
    )
    .run(
      clientScanId,
      openSession ? openSession.id : null,
      location ? location.id : null,
      studentNumber,
      typeof body.clientTime === 'string' ? body.clientTime : null,
      receivedAt,
      req.get('X-Client-Id') || null
    );

  const row = db.prepare('SELECT * FROM attendance WHERE id = ?').get(info.lastInsertRowid);

  events.broadcast('scan', scanEvent(row, location ? location.name : null));
  broadcastSummary();

  res.json({
    ok: true,
    created: true,
    id: row.id,
    clientScanId,
    studentNumber: row.student_number,
    sessionId: row.session_id,
    receivedAt: row.received_at
  });
});

// 誤スキャン取り消し。サーバ上は論理削除(deleted=1)、レコードは残す。
router.post('/cancel', auth.clientAuth, (req, res) => {
  const body = req.body || {};
  const clientScanId = body.clientScanId;
  if (typeof clientScanId !== 'string' || clientScanId.length === 0) {
    return res.status(400).json({ error: 'clientScanId is required' });
  }

  const row = db.prepare('SELECT * FROM attendance WHERE client_scan_id = ?').get(clientScanId);
  if (!row) return res.json({ ok: true, cancelled: false, reason: 'not_found' });
  if (row.deleted) return res.json({ ok: true, cancelled: false, reason: 'already_deleted' });

  db.prepare('UPDATE attendance SET deleted = 1, deleted_at = ? WHERE id = ?').run(nowIso(), row.id);
  events.broadcast('cancel', {
    id: row.id,
    clientScanId,
    studentNumber: row.student_number,
    sessionId: row.session_id,
    locationId: row.location_id
  });
  broadcastSummary();

  res.json({ ok: true, cancelled: true });
});

router.get('/status', auth.clientAuth, (req, res) => {
  res.json(computeSummary());
});

router.get('/locations', auth.clientAuth, (req, res) => {
  res.json(db.prepare('SELECT id, name FROM locations WHERE active = 1 ORDER BY sort, id').all());
});

router.get('/summary', auth.dashboardAuth, (req, res) => {
  res.json(computeSummary());
});

router.get('/stream', auth.dashboardAuth, (req, res) => {
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
