#!/usr/bin/env bash
# 参照ソース（_reference/Implem.Pleasanter。AGPL v3）が、このツールのビルドへ混ざらないことを検査する。
#
# **Template の同名の検査は、`Implem.` という文字列が src / tests に 1 つでもあれば落とす。**
# このツールは、利用者の環境にある Pleasanter のフォルダ（`Implem.Pleasanter`、`Implem.CodeDefiner` と同じ作法）を
# 探す機能を持ち、その名前を文字列として扱う（Options.cs・試験）。それは取り込みではないので、
# ここでは「取り込み」だけを見る。
#   - .csproj / .slnx / Directory.Build.props が、Implem.* や _reference を参照していないこと
#     （ProjectReference・Compile・Reference・Include など）
#   - ソースが Implem.* の名前空間を using / 宣言していないこと
set -euo pipefail
cd "$(dirname "$0")/.."

if grep -REn 'Implem\.|_reference' src tests --include='*.csproj' --include='*.props' --include='*.targets'; then
    echo 'Reference source must not be referenced from a project file.' >&2
    exit 1
fi
if grep -En 'Implem\.|_reference' ./*.slnx Directory.Build.props; then
    echo 'Reference source must not be included in the solution.' >&2
    exit 1
fi
if grep -REn '^[[:space:]]*(using|namespace)[[:space:]]+(static[[:space:]]+)?Implem\.' src tests --include='*.cs'; then
    echo 'Reference source namespaces must not be imported or declared.' >&2
    exit 1
fi
echo 'Reference isolation check passed.'
