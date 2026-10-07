# 本体の画面操作による E2E 手順

開発者向け。Pleasanter 本体を検証用 DB で起動し、画面からサイト・選択肢・保存ビュー・レコードを作ったうえで IndexCreator を適用し、画面の動作と View の内容を確認する。結果は [検証記録](検証記録.md) に残す。実運用の DB には向けない。

## 前提

- Node.js 24 以上と Chrome（Playwright は同梱ブラウザではなくローカルの Chrome を使う）。`npm ci --ignore-scripts` で playwright-core 1.63.0（Apache-2.0、依存なし）を入れる。配布物には含まれない開発専用の依存。
- 固定参照コミットから CodeDefiner と本体をビルドした検証コピー（`temp/` など、リポジトリの管理外）。
- `compose.yaml` の検証用 DB（`docker compose --profile postgres|mysql|sqlserver up -d --wait`）。

## 手順

1. 検証コピーの `App_Data/Parameters` の Rds.json と Service.json を退避し、検証用 DB に向ける。Service.json の Name は専用の名前（例: IndexCreatorE2E）にする。接続文字列は本体の書式で、`Server=...;Database=...;UID=...;PWD=...` の形にする。SQL Server は `Server=127.0.0.1,51433;...;TrustServerCertificate=True`。
2. 検証コピーの CodeDefiner で `_rds /y /p <検証コピーの Implem.Pleasanter>` を実行して本体のスキーマを作る。SQL Server の検証コンテナには全文検索がないため、全文索引の作成だけ失敗する。
3. 本体を起動する。`ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:58800 dotnet Implem.Pleasanter.dll`。別のポートを使う場合は環境変数 `E2E_BASE` で指定する。
4. 作業用の空フォルダで次を実行する。ログイン状態と SiteId は `e2e-state.json` と `e2e-ids.json` としてそのフォルダに書かれる。
   - `node <リポジトリ>/_development/e2e/seed.mjs`: フォルダ・サイト・Wiki、選択肢、保存ビュー、レコードを画面から作る。
5. 本体に向けた IndexCreator を実行する。MySQL の本体 owner には CREATE VIEW がないため、View は `INDEXCREATOR_CONNECTION_STRING` に管理者の接続を指定する。
   - `_rds --min-records 0 -y`、`_views --exclude-tree <サブフォルダ> --exclude-site <トップ> -y`、`_choice-lists` に同じ除外指定。
6. `node <リポジトリ>/_development/e2e/verify.mjs` で、適用前後の一覧、画面からの登録、選択肢を画面で変更した後のドロップダウンを JSON で得る。選択肢 View の中身を DB に問い合わせ、ドロップダウンと一致することを確認する。
7. 終了後に本体を止め、退避した Rds.json と Service.json を戻し、`docker compose --profile '*' down -v` で DB を破棄する。

## 期待する結果

- 除外したフォルダ配下と個別サイトに View が作られない。
- 一覧は保存ビューの条件どおり（状態=受付、数値A 降順）に並ぶ。
- 選択肢 View の Value / Text / TextMini は、本体のドロップダウン（Value と表示名）と一致する。画面で追加した選択肢は再実行なしで反映され、動的参照（`[[Users]]`）の行は含まれない。
- 画面で選択肢を設定すると検索方法が部分一致で保存される場合がある。`seed.mjs` は完全一致に変えるため、分類フィルタの索引が作られる。

## 画面の識別子が変わったとき

操作は本体の要素 ID（`#NewMenuContainer`、`#EditorColumnDialog`、`#ViewFilterSelector` など）とボタン名に依存する。固定参照コミットを更新して失敗した場合は、`lib.mjs` の該当操作を本体の画面に合わせて直す。
