'use strict';

const fs = require('fs');
const path = require('path');
const Database = require('better-sqlite3');
const config = require('./config');

fs.mkdirSync(path.dirname(config.dbPath), { recursive: true });

const db = new Database(config.dbPath);
db.pragma('journal_mode = WAL');
db.pragma('foreign_keys = ON');

db.exec(`
CREATE TABLE IF NOT EXISTS sessions (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  name         TEXT    NOT NULL,
  status       TEXT    NOT NULL DEFAULT 'idle',   -- idle | open | closed
  target_count INTEGER NOT NULL DEFAULT 0,        -- 点呼対象者数 (完了率の分母)
  started_at   TEXT,
  ended_at     TEXT,
  created_at   TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS locations (
  id     INTEGER PRIMARY KEY AUTOINCREMENT,
  name   TEXT    NOT NULL UNIQUE,
  sort   INTEGER NOT NULL DEFAULT 0,
  active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS attendance (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  client_scan_id  TEXT    NOT NULL UNIQUE,        -- クライアント発行UUID (再送の冪等化)
  session_id      INTEGER,                        -- 開いているセッションが無ければ NULL
  location_id     INTEGER,
  student_number  INTEGER NOT NULL,
  client_time     TEXT,
  received_at     TEXT    NOT NULL,               -- サーバ受信時刻(正)
  client_id       TEXT,
  deleted         INTEGER NOT NULL DEFAULT 0,     -- 誤スキャン取り消しフラグ(論理削除)
  deleted_at      TEXT,
  webhook_sent    INTEGER NOT NULL DEFAULT 0      -- Webhook へ転送済みか
);
CREATE INDEX IF NOT EXISTS idx_attendance_session  ON attendance(session_id, deleted);
CREATE INDEX IF NOT EXISTS idx_attendance_location ON attendance(location_id, deleted);
CREATE INDEX IF NOT EXISTS idx_attendance_webhook  ON attendance(webhook_sent, deleted);

CREATE TABLE IF NOT EXISTS settings (
  key   TEXT PRIMARY KEY,
  value TEXT
);
`);

// 既存DBへの後方互換マイグレーション
function ensureColumn(table, column, definition) {
  const columns = db.prepare(`PRAGMA table_info(${table})`).all();
  if (!columns.some((c) => c.name === column)) {
    db.exec(`ALTER TABLE ${table} ADD COLUMN ${column} ${definition}`);
  }
}

ensureColumn('attendance', 'webhook_sent', 'INTEGER NOT NULL DEFAULT 0');

const nowIso = () => new Date().toISOString();

module.exports = { db, nowIso };
