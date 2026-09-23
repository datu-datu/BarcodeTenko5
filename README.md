# 文化祭 学生点呼システム (BarcodeTenko5)

バーコード（キーボードエミュレータ）または手入力で学籍番号を受付し、各点呼場所の
Windows 端末からサーバへ同期、ブラウザでリアルタイムに点呼状況を確認できるシステムです。

```
[Windowsクライアント x N]  --HTTPS POST-->  [OCI VM: Node.js + SQLite]
      (点呼場所ごとに1台)                          |
                                       SSE(リアルタイムpush)
                                                 v
                                        [ブラウザ: 状況 / 管理画面]
                    |
                    +-- Webhook --> Power Automate --> Teams Chat
```

- **クライアント**: C# / .NET 10 (WPF)、ローカル SQLite に未送信キューを保持
- **サーバ**: Node.js (Express) + SQLite (better-sqlite3)
- **通信**: クライアント→サーバの一方向。クライアント間の同期はなし

## ディレクトリ構成

```
BarcodeTenko5/
├─ server/                     Node.js サーバ
│  ├─ src/
│  │  ├─ index.js              エントリポイント
│  │  ├─ config.js             環境変数の読み込み
│  │  ├─ db.js                 SQLite スキーマ
│  │  ├─ code.js               学籍番号の正規化(後方5桁)
│  │  ├─ auth.js               クライアントトークン / 管理Cookie認証
│  │  ├─ events.js             SSE ハブ
│  │  ├─ summary.js            集計
│  │  ├─ webhook.js            Power Automate 転送キュー
│  │  └─ routes/               public.js, admin.js
│  ├─ public/                  ダッシュボード / 管理画面 (HTML+JS)
│  └─ .env.example
└─ client/
   └─ BarcodeTenko.Client/     C# WPF クライアント
      ├─ Models/ Services/ ViewModels/
      ├─ App.xaml (cs) / LocationSelectWindow / MainWindow
      └─ appsettings.json
```

---

## サーバ

### セットアップ

```bash
cd server
cp .env.example .env      # Windows なら copy .env.example .env
# .env を編集して CLIENT_TOKEN / ADMIN_PASSWORD / SESSION_SECRET を設定
npm install
npm start
```

起動後:

- ダッシュボード: `http://<host>:8080/`
- 管理画面: `http://<host>:8080/admin`

### 環境変数 (.env)

| 変数 | 説明 |
| --- | --- |
| `PORT` | 待ち受けポート (既定 8080) |
| `DB_PATH` | SQLite ファイルパス |
| `CLIENT_TOKEN` | クライアントが送る共有トークン。空だと認証なし(非推奨) |
| `ADMIN_PASSWORD` | 管理画面パスワード。空だと管理APIは無効 |
| `SESSION_SECRET` | 管理セッションCookieの署名鍵(長いランダム文字列) |
| `DASHBOARD_TOKEN` | 設定するとダッシュボードに `?token=xxx` が必要 |
| `WEBHOOK_URL` | Power Automate の HTTP トリガー URL(空なら転送しない) |
| `WEBHOOK_SECRET` | Webhook に付ける共有シークレット(任意) |

### 管理画面の使い方

1. 管理者パスワードでログイン
2. **点呼場所**を追加（例: 体育館、視聴覚室）
3. **セッション**を追加（名前と点呼対象者数を入力）→「開始」で `open`
4. 2日間 x 1日2回 = 4 セッションを運用。次のセッションを「開始」すると前のセッションは自動で閉じます
5. 「受付データ」で取消済みを含む全レコードを確認できます

### API 概要

| メソッド | パス | 認証 | 説明 |
| --- | --- | --- | --- |
| POST | `/api/scan` | Client token | スキャン受付 `{clientScanId, locationId, code, clientTime}` |
| POST | `/api/cancel` | Client token | 取消(論理削除) `{clientScanId}` |
| GET | `/api/status` | Client token | 現在のセッション・集計 |
| GET | `/api/locations` | Client token | 有効な点呼場所一覧 |
| GET | `/api/summary` | Dashboard | 集計 |
| GET | `/api/stream` | Dashboard | SSE (stats / scan / cancel / session) |
| POST | `/admin/api/login` | - | 管理者ログイン |
| GET/POST/PATCH/DELETE | `/admin/api/sessions` | Admin | セッション管理 |
| GET/POST/PATCH/DELETE | `/admin/api/locations` | Admin | 点呼場所管理 |
| GET | `/admin/api/scans` | Admin | 受付データ一覧 |
| GET | `/admin/api/stream` | Admin | 管理画面用 SSE |

`code` は 10桁(バーコード)でも 5桁(手入力)でもよく、**後方5桁**を学籍番号として採用します。
`clientScanId` はクライアント発行の UUID で、再送時に二重登録されません（冪等）。

### サーバのデプロイ（Ubuntu / systemd / Caddy）

Linux（OCI の Ubuntu VM 等）で常駐させる手順は [DEPLOY.md](DEPLOY.md) にまとめています。
systemd ユニットと Caddyfile のひな形は [`deploy/`](deploy/) にあります。

- **Node.js 22 以降が必要**です（`better-sqlite3` v13 の要件。Node 20 では動作しません）
- 外部公開は **Caddy 経由の 80/443 のみ**。アプリの 8080 は開放しないでください
- 学籍番号は個人情報のため、必ず HTTPS で公開してください

### Teams Webhook 連携 (無料ワークフロー対応)

`WEBHOOK_URL` に Microsoft Teams の無料ワークフロー（「Webhook 要求を受信したときにチャネルに投稿する」）の URL を設定すると、受付した点呼データをバッチ（複数件まとめ）で Teams チャネルへ Adaptive Card 形式で自動投稿します。

設定手順の詳細は [TEAMS_WEBHOOK_GUIDE.md](TEAMS_WEBHOOK_GUIDE.md) を参照してください。

---

## クライアント

### ビルド

```bash
cd client
dotnet build BarcodeTenko.slnx -c Release
```

### 配布用の単一ファイル出力

```bash
dotnet publish client/BarcodeTenko.Client/BarcodeTenko.Client.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

出力: `client/BarcodeTenko.Client/bin/Release/net10.0-windows/win-x64/publish/BarcodeTenko.exe`
（.NET ランタイム不要。`appsettings.json` も一緒に配置されます）

### appsettings.json

```json
{
  "ServerUrl": "https://tenko.example.com",
  "ClientToken": ".env の CLIENT_TOKEN と同じ値",
  "ClientId": "",
  "DataDirectory": "kunugidasaitenko/data",
  "OutputDirectory": "kunugidasaitenko/bin",
  "SoundEnabled": true,
  "Locations": [ { "Id": 1, "Name": "体育館" } ]
}
```

- `ClientId` は空でOK。初回起動時に自動生成してローカルに保存します
- `Locations` はサーバに接続できないときのフォールバック。通常はサーバから取得します
- `DataDirectory` … ローカル SQLite の保存先 / `OutputDirectory` … bin ファイルの出力先（親フォルダー `kunugidasaitenko/` 内に生成されます）

### 操作

1. 起動 → 点呼場所を選択
2. バーコードをかざす or 学籍番号(下5桁)を入力して Enter
3. 画面下に「点呼済み / 対象者数」と完了率が表示されます
4. 誤入力は一覧の「取り消し」ボタンで取消（サーバ側は取消フラグで記録が残ります）
5. セッション終了時に「点呼完了」を押すと bin ファイルを出力し、エクスプローラーで表示

### bin ファイル形式

- ファイル名: `tenko_<点呼場所>_<yyyyMMdd_HHmmss>.bin`
- 中身: 学籍番号(5桁)を **UInt16・リトルエンディアン**で並べただけ。ヘッダや件数はなし
- 出力対象はそのセッション中に受付した（取消していない）学籍番号

---

## 仕様メモ（確定事項）

- 学籍番号は最大 26300 のため UInt16 で表現可能（上限 65535 を超える入力は拒否）
- セッション開始前・終了後のスキャンも **サーバには常に記録**（`session_id = NULL`、集計対象外）。クライアントは警告しません
- 取消はサーバ側で `deleted = 1` の論理削除。クライアント側は物理削除
- 同一学籍番号の重複チェックは行いません（受付件数をそのまま加算）
- 対象者数はセッション単位で設定し、完了率 = 点呼済み / 対象者数
- 時刻はサーバ受信時刻を正とします

## セキュリティ

- 学籍番号は個人情報のため **必ず HTTPS** で公開してください（Caddy 等で TLS 終端）
- クライアントは `CLIENT_TOKEN`、管理画面は `ADMIN_PASSWORD` で保護されます。既定値のまま公開しないでください
- ダッシュボードを限定公開したい場合は `DASHBOARD_TOKEN` を設定してください

### 既知の依存脆弱性について

`SQLitePCLRaw.lib.e_sqlite3` (クライアントの Microsoft.Data.Sqlite が使用) に
CVE-2025-6965 の警告 (NU1903) が出ますが、執筆時点で**修正版がリリースされていません**。
本アプリはローカルの固定SQLのみを実行し、外部から任意SQLを受け付けないため実害はありません。
修正版が公開されたら Microsoft.Data.Sqlite / SQLitePCLRaw を更新してください。
