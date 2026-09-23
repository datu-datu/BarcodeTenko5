'use strict';

const path = require('path');
const express = require('express');

const config = require('./config');
const auth = require('./auth');
const webhook = require('./webhook');

const app = express();
app.disable('x-powered-by');
app.set('trust proxy', 1);
app.use(express.json({ limit: '256kb' }));
app.use(auth.parseCookies);
app.use(express.static(path.join(__dirname, '..', 'public')));

app.use('/api', require('./routes/public'));
app.use('/admin/api', require('./routes/admin'));

app.get('/admin', (req, res) => res.sendFile(path.join(__dirname, '..', 'public', 'admin.html')));
app.get('/healthz', (req, res) => res.json({ ok: true, time: new Date().toISOString() }));

app.use((req, res) => res.status(404).json({ error: 'not found' }));

// eslint-disable-next-line no-unused-vars
app.use((err, req, res, next) => {
  console.error(err);
  res.status(500).json({ error: 'internal server error' });
});

app.listen(config.port, () => {
  console.log(`BarcodeTenko server listening on http://0.0.0.0:${config.port}`);
  console.log(`  ダッシュボード : http://localhost:${config.port}/`);
  console.log(`  管理画面       : http://localhost:${config.port}/admin`);
  if (!config.clientToken) console.warn('[warn] CLIENT_TOKEN 未設定 - クライアントAPIは認証なしです。');
  if (!config.adminPassword) console.warn('[warn] ADMIN_PASSWORD 未設定 - 管理APIは無効です。');
  if (!config.sessionSecret) console.warn('[warn] SESSION_SECRET 未設定 - 管理画面は無効です。');
  if (!config.dashboardToken) console.warn('[warn] DASHBOARD_TOKEN 未設定 - ダッシュボードは公開です。');
  if (!config.webhookUrl) console.log('[info] WEBHOOK_URL 未設定 - Webhook転送は無効です。');
});

webhook.start();
