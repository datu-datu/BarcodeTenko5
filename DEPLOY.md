# サーバ デプロイ手順（Ubuntu / systemd / Caddy）

BarcodeTenko5 のサーバ（Node.js + SQLite）を Linux 上で常駐（デーモン化）させる手順です。
Ubuntu の VM（OCI 等）を想定し、**systemd で自動起動・自動再起動**、**Caddy で HTTPS 終端**する構成にします。

---

## 1. 構成

```
[Windowsクライアント x N]
        │ HTTPS POST (443)
        ▼
   [Caddy]  ← TLS終端・リバースプロキシ
        │ http://127.0.0.1:8080
        ▼
   [node src/index.js]  ← systemd が常駐管理
        │
        ▼
   /opt/barcode-tenko/server/data/tenko.db (SQLite)
```

- `8080` は**外部に公開しません**。外部からは Caddy の 80/443 のみ開放します。
- 学籍番号は個人情報のため、**必ず HTTPS で公開**してください。

### 配置先

| リポジトリ内 | サーバー上の設置先 |
| --- | --- |
| `deploy/barcode-tenko.service` | `/etc/systemd/system/barcode-tenko.service` |
| `deploy/Caddyfile` | `/etc/caddy/Caddyfile` |
| `server/` 一式 | `/opt/barcode-tenko/server/`（git clone を推奨） |
| `server/.env.example` | `/opt/barcode-tenko/server/.env`（実値に編集・git 管理外） |
| （自動生成） | `/opt/barcode-tenko/server/data/`（DB・git 管理外） |

以降のコマンドはすべてサーバー上で実行します。

---

## 2. Node.js 22+ をシステムワイドに導入

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl gnupg
curl -fsSL https://deb.nodesource.com/setup_24.x | sudo -E bash -
sudo apt-get install -y nodejs build-essential python3

command -v node npm     # => /usr/bin/node, /usr/bin/npm を確認
```

**要件は Node.js 22 以降**です。`better-sqlite3` は v13 で N-API 化されており、Node 22 以降の prebuilt バイナリが同梱されます（Node 20 では動作しません）。v13 はインストールスクリプトを持たないため `node-gyp` は走りませんが、フォールバック用に `build-essential` と `python3` を入れておきます。

> **nvm 等でユーザー領域に Node を入れないでください。**
> `sudo` は `secure_path` により PATH を固定するため `sudo -u tenko npm` が見つからず、かつ `/home` 配下は下記ユニットの `ProtectHome=true` で読めないため起動もできません。

> **npm のアップグレードは不要です。** npm 12 系は Node `^22.22.2 || ^24.15.0 || >=26` を要求するため、Node 24.14.x では `npm install -g npm@12` が `EBADENGINE` で失敗します。NodeSource 同梱の npm 11 のままで問題ありません。

---

## 3. サービスユーザーと配置

専用のシステムユーザーで動かします（root で動かさない）。

```bash
sudo useradd --system --create-home --shell /usr/sbin/nologin tenko
sudo mkdir -p /opt/barcode-tenko
sudo git clone https://github.com/datu-datu/BarcodeTenko5.git /opt/barcode-tenko
sudo chown -R tenko:tenko /opt/barcode-tenko
```

git clone にしておくと、以降の更新が `git pull` で済みます（リポジトリが非公開の場合は、デプロイ用の鍵かトークンを用意するか、`scp` でファイルを送ってください）。

---

## 4. 依存パッケージのインストール

```bash
cd /opt/barcode-tenko/server
sudo -u tenko npm ci --omit=dev
```

`package-lock.json` に固定されたバージョンが入ります。`package.json` だけ更新して lock を更新し忘れると `EUSAGE`（ロック不整合）で停止するので、**必ず両方**を揃えてください。

ネイティブモジュールが正しくロードできるか確認します。

```bash
sudo -u tenko node -e "const D=require('better-sqlite3');const d=new D(':memory:');d.exec('create table t(x)');d.prepare('insert into t values (?)').run(1);console.log('OK',require('better-sqlite3/package.json').version,d.prepare('select x from t').get());"
# => OK 13.0.3 { x: 1 }
```

---

## 5. `.env` の作成

```bash
cd /opt/barcode-tenko/server
sudo -u tenko cp .env.example .env
sudo -u tenko sed -i "s|^SESSION_SECRET=.*|SESSION_SECRET=$(openssl rand -hex 32)|" .env
sudo -u tenko sed -i "s|^CLIENT_TOKEN=.*|CLIENT_TOKEN=$(openssl rand -hex 24)|" .env
sudo -u tenko sed -i "s|^ADMIN_PASSWORD=.*|ADMIN_PASSWORD=<管理画面のパスワード>|" .env
sudo chmod 600 .env
sudo chown tenko:tenko .env
```

- `CLIENT_TOKEN` は **Windows クライアントの `appsettings.json` と同一の値**にしてください。
- `ADMIN_PASSWORD` / `SESSION_SECRET` は必ず既定値から変更します。
- この `.env` は systemd の `EnvironmentFile` として読み込まれます。`export` は書かないでください（`KEY=value` 形式のみ）。
- `dotenv` は既存の環境変数を上書きしないため、`EnvironmentFile` の値が優先されます。
- `.env` は `.gitignore` 済みです。`git pull` で上書きされることはありません。

---

## 6. データベースディレクトリの作成（必須）

```bash
sudo install -d -o tenko -g tenko -m 700 /opt/barcode-tenko/server/data
sudo ls -ld /opt/barcode-tenko/server/data   # => tenko tenko
```

このユニットは `ProtectSystem=strict` で**ファイルシステム全体を読み取り専用**にし、DB を書く `data/` だけを `ReadWritePaths` で許可しています。systemd はマウント名前空間を**プロセス起動前**に構築するため、`ReadWritePaths` のパスが実在しないと `status=226/NAMESPACE` で起動に失敗します。

`data/` は `src/db.js` が起動時に自動生成しますが、それは名前空間の構築より後なので間に合いません。**クリーンデプロイや更新のたびに、起動前にこのコマンドを実行**してください。

---

## 7. systemd に登録

```bash
sudo cp /opt/barcode-tenko/deploy/barcode-tenko.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now barcode-tenko
systemctl status barcode-tenko --no-pager
journalctl -u barcode-tenko -n 30 --no-pager
```

`BarcodeTenko server listening on http://0.0.0.0:8080` と、続くダッシュボード / 管理画面の URL がログに出れば起動成功です。

`systemctl enable` により**サーバー再起動後も自動で起動**し、`Restart=always` により**異常終了時も自動復帰**します。

### ユニットの要点

| 設定 | 意味 |
| --- | --- |
| `User=tenko` / `Group=tenko` | root ではなく専用ユーザーで実行 |
| `WorkingDirectory=/opt/barcode-tenko/server` | `dotenv` と `DB_PATH` はこのディレクトリ基準で解決される |
| `EnvironmentFile=.../.env` | 秘密情報をユニットに直書きしない |
| `ExecStart=/usr/bin/node src/index.js` | `command -v node` の結果と一致させること |
| `Restart=always` / `RestartSec=3` | クラッシュ時は 3 秒後に再起動 |
| `ProtectSystem=strict` + `ReadWritePaths=.../data` | 書き込みを DB ディレクトリだけに限定 |

---

## 8. Caddy で HTTPS 終端

```bash
sudo apt-get install -y caddy
sudo cp /opt/barcode-tenko/deploy/Caddyfile /etc/caddy/Caddyfile
sudo nano /etc/caddy/Caddyfile     # tenko.example.com を実際の FQDN に書き換え
sudo systemctl reload caddy
```

対象ドメインの **A レコードをこのサーバーの IP に向けておけば、Caddy が Let's Encrypt の証明書を自動取得・自動更新**します。HTTP/HTTPS（80/443）が外部から到達できる必要があります。

アプリ側は `trust proxy` を有効にしているため、Caddy 経由でもクライアント IP と `secure` Cookie が正しく扱われます。

---

## 9. ファイアウォール

- **OCI のセキュリティリスト（または ufw）で 80/443 のみ開放**してください。
- **8080 は開放しないでください。** アプリは `0.0.0.0:8080` で待ち受けますが、外部公開は Caddy 経由のみとするのが前提です。

ufw を使う場合は、SSH を閉め出さないよう順序に注意してください。

```bash
sudo ufw allow OpenSSH
sudo ufw allow 80,443/tcp
sudo ufw enable
```

---

## 10. 動作確認

```bash
# サーバー内部（アプリ直）
curl -s http://127.0.0.1:8080/healthz
# => {"ok":true,"time":"..."}

# 外部（Caddy 経由・TLS）
curl -sI https://tenko.example.com/healthz | head -1
# => HTTP/2 200

# サービス状態とログ
systemctl status barcode-tenko --no-pager
journalctl -u barcode-tenko -f
```

ブラウザで `https://<ドメイン>/`（ダッシュボード）と `https://<ドメイン>/admin`（管理画面）を開き、管理画面のパスワードでログインできることを確認します。

---

## 11. 更新手順

```bash
sudo systemctl stop barcode-tenko
sudo -u tenko git -C /opt/barcode-tenko pull
cd /opt/barcode-tenko/server
sudo -u tenko npm ci --omit=dev
sudo install -d -o tenko -g tenko -m 700 /opt/barcode-tenko/server/data   # 6章の再実行
sudo systemctl start barcode-tenko
journalctl -u barcode-tenko -n 30 --no-pager
```

`.env` と `data/` は git 管理外なので、更新しても消えません。`data/tenko.db` は WAL モードで運用されます。

---

## 12. 運用手順

```bash
systemctl status barcode-tenko            # 状態
sudo systemctl restart barcode-tenko      # 再起動
sudo systemctl stop barcode-tenko         # 停止
journalctl -u barcode-tenko -f            # ログ追尾（標準出力/標準エラーがそのまま入る）
journalctl -u barcode-tenko --since today # 当日分
```

### DB のバックアップ

```bash
sudo apt-get install -y sqlite3
sudo -u tenko sqlite3 /opt/barcode-tenko/server/data/tenko.db ".backup /tmp/tenko-$(date +%F).db"
sudo mv /tmp/tenko-$(date +%F).db /var/backups/   # 任意
```

WAL モードのため、稼働中に `tenko.db` を単純コピーするのは避け、上記の `.backup`（オンラインバックアップ）を使ってください。受付データはこの DB にしか存在しないため、文化祭期間中は定期的に取得してください。

---

## 13. トラブルシューティング

| 症状 | 原因と対処 |
| --- | --- |
| `status=226/NAMESPACE` で起動失敗 | `ReadWritePaths` の `data/` が無い。第6章の `install -d` を実行してから再起動。 |
| `sudo: npm: command not found` | Node がユーザー領域（nvm 等）にあり `sudo` の `secure_path` に入っていない。第2章のとおりシステムワイドに導入する。 |
| `npm error code EUSAGE`（npm ci） | `package.json` と `package-lock.json` が不一致。両方を揃えてから `npm ci`。 |
| `npm error code EBADENGINE`（npm の更新時） | npm 12 は Node 24.15 以降が必要。**npm は上げない**（アプリの動作には無関係）。 |
| `Could not locate the bindings file` / better-sqlite3 のロード失敗 | `node_modules` を Windows から持ち込んだ、または Node のバージョン不一致。Linux 上で `npm ci --omit=dev` をやり直す。 |
| `install-scripts ... not yet covered by allowScripts` 警告 | npm の新しいスクリプト承認機能。better-sqlite3 v13 はインストールスクリプトを持たないため出ない。v11 等で出る場合は `npm install-scripts approve better-sqlite3` のうえ `npm rebuild better-sqlite3`。 |
| `ProtectHome=true` で起動しない | Node や `server/` が `/home` 配下にある。`/opt` と `/usr/bin` に配置する。 |
| 外部から 8080 に接続できない | 設計どおり（Caddy 経由のみ公開）。 |
| SSE の反映が遅い | `deploy/Caddyfile` の `flush_interval -1` を有効化して `systemctl reload caddy`。 |
