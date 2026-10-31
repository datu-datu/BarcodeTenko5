'use strict';

const path = require('path');
require('dotenv').config();

function resolveFromRoot(p) {
  if (!p) return p;
  return path.isAbsolute(p) ? p : path.join(__dirname, '..', p);
}

function intFromEnv(name, fallback) {
  const n = Number.parseInt(process.env[name] || '', 10);
  return Number.isFinite(n) && n > 0 ? n : fallback;
}

const config = {
  port: Number.parseInt(process.env.PORT || '8080', 10),
  dbPath: resolveFromRoot(process.env.DB_PATH || './data/tenko.db'),
  clientToken: process.env.CLIENT_TOKEN || '',
  adminPassword: process.env.ADMIN_PASSWORD || '',
  sessionSecret: process.env.SESSION_SECRET || '',
  dashboardToken: process.env.DASHBOARD_TOKEN || '',
  webhookUrl: process.env.WEBHOOK_URL || '',
  webhookSecret: process.env.WEBHOOK_SECRET || '',
  webhookBatchSize: intFromEnv('WEBHOOK_BATCH_SIZE', 10),
  webhookIntervalMs: intFromEnv('WEBHOOK_INTERVAL_MS', 300000)
};

module.exports = config;
