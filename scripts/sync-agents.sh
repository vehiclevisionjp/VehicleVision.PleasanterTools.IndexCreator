#!/usr/bin/env bash
# 共通規約の本文を複写し、.github/ 基準の相対リンクを直下基準へ直す。
set -euo pipefail
cd "$(dirname "$0")/.."

build() {
    cat <<'HEADER'
# エージェント向け指示

作業前に [.github/copilot-instructions.md](.github/copilot-instructions.md) を全文読むこと。
要件と検証状況は [_documents/アーキテクチャ方針.md](_documents/アーキテクチャ方針.md) を確認する。

<!-- 以下は .github/copilot-instructions.md から生成する。直接編集しないこと。
     編集後は bash scripts/sync-agents.sh を実行する。 -->
HEADER
    awk '
        /^<!-- AGENT-RULES:BEGIN/ { header = 1; next }
        header && /-->/ { header = 0; body = 1; next }
        header { next }
        /^<!-- AGENT-RULES:END/ { body = 0; next }
        body { print }
    ' .github/copilot-instructions.md |
        sed -E \
            -e 's#\]\(([^)/:#][^):]*)\)#](.github/\1)#g' \
            -e 's#\]\(\.github/\.\./#](#g'
}

if [[ "${1:-}" == --check ]]; then
    diff -u AGENTS.md <(build)
    echo 'Agent instructions are synchronized.'
else
    build > AGENTS.md
    echo 'Agent instructions generated.'
fi
