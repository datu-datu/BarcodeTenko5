'use strict';

const path = require('path');
require('dotenv').config();

function resolveFromRoot(p) {
  if (!p) return p;
  return path.isAbsolute(p) ? p : path.join(__dirname, '..', p);
}

const config = {
  port: Number.parseInt(process.env.PORT || '8080', 10),
  dbPath: resolveFromRoot(process.env.DB_PATH || './data/tenko.db'),
  clientToken: process.env.CLIENT_TOKEN || '',
  adminPassword: process.env.ADMIN_PASSWORD || '',
  sessionSecret: process.env.SESSION_SECRET || 'insecure-default-session-secret',
  dashboardToken: process.env.DASHBOARD_TOKEN || '',
  webhookUrl: process.env.WEBHOOK_URL || '',
  webhookSecret: process.env.WEBHOOK_SECRET || '',
  webhookBatchSize: Number.parseInt(process.env.WEBHOOK_BATCH_SIZE || '10', 10),
  webhookIdleMs: Number.parseInt(process.env.WEBHOOK_IDLE_MS || '10000', 10)
};

module.exports = config;
