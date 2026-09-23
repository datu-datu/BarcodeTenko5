'use strict';

const crypto = require('crypto');
const config = require('./config');

const SESSION_COOKIE = 'tenko_admin';
const SESSION_TTL_MS = 12 * 60 * 60 * 1000;

function safeEqual(a, b) {
  const ab = Buffer.from(String(a));
  const bb = Buffer.from(String(b));
  if (ab.length !== bb.length) return false;
  return crypto.timingSafeEqual(ab, bb);
}

function parseCookies(req, res, next) {
  req.cookies = {};
  const header = req.headers.cookie;
  if (header) {
    for (const part of header.split(';')) {
      const idx = part.indexOf('=');
      if (idx > -1) {
        const key = part.slice(0, idx).trim();
        const value = part.slice(idx + 1).trim();
        if (key) req.cookies[key] = decodeURIComponent(value);
      }
    }
  }
  next();
}

function sign(value) {
  return crypto.createHmac('sha256', config.sessionSecret).update(value).digest('hex');
}

function makeSessionCookie() {
  const ts = String(Date.now());
  return `${ts}.${sign(ts)}`;
}

function verifySessionCookie(value) {
  if (!config.sessionSecret) return false;
  if (!value) return false;
  const idx = value.lastIndexOf('.');
  if (idx < 0) return false;
  const ts = value.slice(0, idx);
  const sig = value.slice(idx + 1);
  if (!safeEqual(sig, sign(ts))) return false;
  const age = Date.now() - Number(ts);
  return age >= 0 && age < SESSION_TTL_MS;
}

// クライアント(Windowsソフト)用の共有トークン認証
function clientAuth(req, res, next) {
  if (!config.clientToken) return next();
  const token = req.get('X-Client-Token') || req.query.token;
  if (token && safeEqual(token, config.clientToken)) return next();
  return res.status(401).json({ error: 'unauthorized' });
}

// 管理画面用のCookie認証
function adminAuth(req, res, next) {
  if (!config.adminPassword) return res.status(503).json({ error: 'admin password is not configured' });
  if (!config.sessionSecret) return res.status(503).json({ error: 'session secret is not configured' });
  if (verifySessionCookie(req.cookies && req.cookies[SESSION_COOKIE])) return next();
  return res.status(401).json({ error: 'unauthorized' });
}

// 状況ダッシュボード用(任意トークン)
function dashboardAuth(req, res, next) {
  if (!config.dashboardToken) return next();
  const token = req.query.token || (req.cookies && req.cookies['tenko_dashboard']);
  if (token && safeEqual(token, config.dashboardToken)) return next();
  return res.status(401).json({ error: 'unauthorized' });
}

// --- ブルートフォース対策 (管理画面ログインのレートリミット) ---
const LOGIN_MAX_FAILURES = 5;            // 最大連続失敗回数
const LOGIN_LOCKOUT_MS = 5 * 60 * 1000;  // ロックアウト時間 (5分)
const LOGIN_FAIL_DELAY_MS = 1000;        // 失敗時の強制遅延 (1秒)

const loginAttempts = new Map();

function cleanOldAttempts() {
  const now = Date.now();
  for (const [ip, info] of loginAttempts.entries()) {
    if (now - info.lastAttempt > LOGIN_LOCKOUT_MS * 2) {
      loginAttempts.delete(ip);
    }
  }
}

function checkLoginRateLimit(ip) {
  cleanOldAttempts();
  const info = loginAttempts.get(ip);
  if (!info) return { locked: false };

  const now = Date.now();
  if (info.lockedUntil && info.lockedUntil > now) {
    const retryAfterSeconds = Math.ceil((info.lockedUntil - now) / 1000);
    return { locked: true, retryAfterSeconds };
  }

  if (info.lockedUntil && info.lockedUntil <= now) {
    loginAttempts.delete(ip);
  }

  return { locked: false };
}

async function recordLoginFailure(ip) {
  const now = Date.now();
  const info = loginAttempts.get(ip) || { failures: 0, lockedUntil: 0, lastAttempt: now };
  info.failures += 1;
  info.lastAttempt = now;

  if (info.failures >= LOGIN_MAX_FAILURES) {
    info.lockedUntil = now + LOGIN_LOCKOUT_MS;
  }
  loginAttempts.set(ip, info);

  // 総当たり攻撃を減速させるため1秒待機
  await new Promise((resolve) => setTimeout(resolve, LOGIN_FAIL_DELAY_MS));
}

function recordLoginSuccess(ip) {
  loginAttempts.delete(ip);
}

module.exports = {
  SESSION_COOKIE,
  SESSION_TTL_MS,
  safeEqual,
  parseCookies,
  makeSessionCookie,
  verifySessionCookie,
  clientAuth,
  adminAuth,
  dashboardAuth,
  checkLoginRateLimit,
  recordLoginFailure,
  recordLoginSuccess
};
