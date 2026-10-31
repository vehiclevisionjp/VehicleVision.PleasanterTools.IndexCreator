# エージェント向け指示

作業前に [.github/copilot-instructions.md](.github/copilot-instructions.md) を全文読むこと。
要件と検証状況は [_documents/アーキテクチャ方針.md](_documents/アーキテクチャ方針.md) を確認する。

<!-- 以下は .github/copilot-instructions.md から生成する。直接編集しないこと。
     編集後は bash scripts/sync-agents.sh を実行する。 -->

応答、ドキュメント、Issue、PR、コミットは日本語。実行時のコンソール出力は英語固定。DB の例外メッセージや資格情報はコンソールへ出さない。

コンソールを英語にする理由は、Azure の開発者コンソール（Kudu）のエンコーディング制約で日本語が文字化けするため。
利用者向け資料にはこの実装上の理由を混ぜず、開発ガイドに記載する。

.NET 10、デュアルライセンス（AGPL-3.0-or-later ＋ 商用。[LICENSING.md](LICENSING.md)）。
Pleasanter 本体は AI の事実確認専用の非追跡ソース。取り込むと商用側を提供できなくなる。
依存 OSS は寛容ライセンス（MIT / BSD 系 / Apache-2.0）に限定する（例外は SNI.runtime のみ。LICENSING.md）。
必要時に `bash scripts/sync-reference.sh` で上流 `main` の先頭を取得してから読む。
既定先は `_reference/Implem.Pleasanter`。
`PLEASANTER_REFERENCE_DIR` を指定した場合は、その取得先を直接読む（リンクは作らない）。
コピー・流用・リンク・プロジェクト参照せず、ビルド・配布物・CI に含めない。
文書には確認した完全なコミット SHA・ソースの固定リンク・参照日を残し、実機未検証なら明記する。
取得スクリプトの英語コンソール出力は規約どおりとする。

`develop` が開発基準、`master` はリリース用。
作業に着手する前に、これからやること・やるべきこと（作業中に見つけたものを含む）を、先に Issue にする。会話の「残りの作業」も Issue に起こし、リポジトリをまたぐ作業は、中心のリポジトリに親の Issue を起こして各リポジトリの Issue から参照する。
初期配置後は Issue → develop から作業ブランチ → Release ビルドと影響するテスト → push →
Draft PR（WIP:、Closes #N）。利用者の DB を変更する機能のマージはレビューを待つ。
並行作業は `.worktrees/` 内の `git worktree`（`.gitignore` 済み）で行う。

正式な要件は `_documents/` に置く。共通規約はこのファイルが単一の参照元。
AGENTS.md は `bash scripts/sync-agents.sh` で再生成し、`--check` で同期を確認する。
CLAUDE.md からも参照する。
共有メモリは Internal の `VehicleVision.PleasanterTools.ClaudeMemory` の `IndexCreator/` フォルダを使い、資格情報は記録しない。
メモリと正式規約が食い違ったら正式規約を優先する。

README は利用者向けの製品説明と操作の入口。導入・利用・更新運用も利用者向け資料へ置く。
ビルド・テスト・CI・設計・エージェント・リリースは開発者向け資料へ分離する。
出典確認・実機検証・セキュリティ調査の記録は `_development/` に置き、利用手順へ混ぜない。
文書を追加したら `_documents/README.md` の該当する入口から案内する。

管理対象は固定接頭辞と定義ハッシュを持つインデックスだけ。標準索引、列、テーブルは変更しない。
解析失敗・欠損した列・定義衝突・同時実行では変更を止める。
削除は明示オプションだけで、新規作成の成功後に行う。

外部仕様は一次情報で確認し参照日と URL を設計資料に残す。未検証の DB 動作を検証済みと書かない。依存追加時はライセンスとロックファイルを確認する。

GitHub Actions はユーザー指定により @v7 などのメジャーバージョンタグで指定し、SHA 固定はしない。
選択肢マスタは項目ごとの View とし、列は本体 Choice と同じ Value / Text / TextMini の3列に固定する。

`if` / `else` / `for` / `foreach` / `while` / `using` / `lock` などの本文の `{}` は省略しない。
