'use strict';

const { db, nowIso } = require('./db');

/**
 * ダッシュボード/クライアント表示用の集計を生成する。
 * 対象者数(分母)はセッション単位の target_count を使用する。
 * セッション未割当(session_id IS NULL)のスキャンは集計から除外し、別途 preSessionTotal で返す。
 */
function computeSummary() {
  const active =
    db.prepare("SELECT * FROM sessions WHERE status = 'open' ORDER BY id DESC LIMIT 1").get() ||
    db.prepare('SELECT * FROM sessions ORDER BY id DESC LIMIT 1').get();

  let total = 0;
  let target = 0;
  let byLocation = [];
  let unassignedLocation = 0;

  if (active) {
    total = db.prepare('SELECT COUNT(*) AS c FROM attendance WHERE deleted = 0 AND session_id = ?').get(active.id).c;
    target = active.target_count || 0;
    byLocation = db
      .prepare(
        `SELECT l.id AS locationId, l.name AS name, COUNT(a.id) AS count
           FROM locations l
           LEFT JOIN attendance a
             ON a.location_id = l.id AND a.deleted = 0 AND a.session_id = ?
          WHERE l.active = 1
          GROUP BY l.id
          ORDER BY l.sort, l.id`
      )
      .all(active.id)
      .map((r) => ({ locationId: r.locationId, name: r.name, count: r.count }));
    unassignedLocation = db
      .prepare('SELECT COUNT(*) AS c FROM attendance WHERE deleted = 0 AND session_id = ? AND location_id IS NULL')
      .get(active.id).c;
  }

  const preSessionTotal = db.prepare('SELECT COUNT(*) AS c FROM attendance WHERE deleted = 0 AND session_id IS NULL').get().c;
  const webhookPending = db.prepare('SELECT COUNT(*) AS c FROM attendance WHERE webhook_sent = 0 AND deleted = 0').get().c;
  const webhookEnabled = (db.prepare("SELECT value FROM settings WHERE key = 'webhook_enabled'").get() || { value: '1' }).value === '1';

  return {
    serverTime: nowIso(),
    session: active
      ? {
          id: active.id,
          name: active.name,
          status: active.status,
          targetCount: active.target_count,
          startedAt: active.started_at,
          endedAt: active.ended_at
        }
      : null,
    total,
    target,
    rate: target > 0 ? total / target : null,
    byLocation,
    unassignedLocation,
    preSessionTotal,
    webhookPending,
    webhookEnabled
  };
}

module.exports = { computeSummary };
