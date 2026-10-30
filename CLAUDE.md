# Claude Code 向け指示

@.github/copilot-instructions.md

要件と検証状況は `_documents/アーキテクチャ方針.md` を読む。
Pleasanter の挙動を調べる前に `bash scripts/sync-reference.sh` で上流 `main` を取得する。
既定先は非追跡の `_reference/Implem.Pleasanter`。
`PLEASANTER_REFERENCE_DIR` があればその取得先を直接読み、リンクは作らない。
参照ソースはビルド・配布に含めず、完全な SHA・固定リンク・参照日と実機未検証の範囲を記録する。
共有メモリは [Internal リポジトリ](https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.IndexCreator.ClaudeMemory)。正式な規約は本体に残す。
