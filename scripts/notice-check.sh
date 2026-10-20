#!/usr/bin/env bash
# NOTICE の依存一覧が、実際の依存と一致しているかを検査する。
#
# **デュアルライセンス（AGPL-3.0-or-later OR 商用）の前提を機械で守るための検査。**
# 依存が寛容ライセンス（MIT / BSD 系 / Apache-2.0）だけであることは人が確かめるしかないが、
# 「NOTICE に載っていない依存がある」「版数がずれている」は機械で見つけられる。
# 見落とすと、リリース物のライセンス表記が実物と食い違う（_documents/リリース手順書.md
# 「リリース前の確認」）。
#
# 検査するもの
#   - src / tests の .csproj の PackageReference（Include と Version）
#   - ルートの package.json（markdownlint など。配布物には入らない開発用） の dependencies / devDependencies
# NOTICE の該当行が「名称 バージョン / ライセンス / 入手元」の形であることを前提に、
# 「名称 バージョン /」を含む行があるかを見る。
#
# **推移的な依存までは見ない。** NOTICE は直接採用したものを記す文書であり、
# 推移的な依存は上流の NOTICE が受け持つ。脆弱性の方は推移的な依存も見ている
# （scripts/nuget-vulnerable-check.sh）。
set -euo pipefail
cd "$(dirname "$0")/.."

NOTICE_FILE="NOTICE"
FRONTEND_PACKAGE_JSON="package.json"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

fail=0

# NuGet の直接依存を「名称<TAB>版」で書き出す。
# **obj/ 配下は見ない。** 復元で生成される .props にも似た記述が現れる
find src tests -name '*.csproj' -not -path '*/obj/*' -print0 \
    | { xargs -0 grep -hoE '<PackageReference[^>]*Include="[^"]+"[^>]*Version="[^"]+"' || true; } \
    | sed -E 's/.*Include="([^"]+)".*Version="([^"]+)".*/\1\t\2/' \
    | sort -u > "$work/nuget.tsv"

# npm の直接依存を「名称<TAB>版」で書き出す。
# dependencies / devDependencies の節だけを見て、値が版らしいものに限る
sed -n '/"\(dev\)\{0,1\}[Dd]ependencies"[[:space:]]*:[[:space:]]*{/,/^[[:space:]]*}/p' \
    "$FRONTEND_PACKAGE_JSON" \
    | grep -oE '"[^"]+"[[:space:]]*:[[:space:]]*"[~^]?[0-9][^"]*"' \
    | sed -E 's/"([^"]+)"[[:space:]]*:[[:space:]]*"[~^]?([^"]+)"/\1\t\2/' \
    | sort -u > "$work/npm.tsv"

check_list() {
    local kind="$1" list="$2" name version
    echo "== ${kind} の直接依存が NOTICE に載っているか =="
    if [ ! -s "$list" ]; then
        if [ "$kind" = NuGet ]; then
            # このリポジトリは標準ライブラリだけで書く（外部パッケージを増やさない）ので、直接依存が無いのが正常
            echo "  （NuGet の直接依存は無い）"
            return
        fi
        echo "NG: ${kind} の依存を 1 つも拾えなかった。抽出のしかたが実物と合っていない" >&2
        fail=1
        return
    fi
    while IFS=$'\t' read -r name version; do
        [ -n "$name" ] && [ -n "$version" ] || continue
        # 「名称 版 / …」と「名称 版 - …」のどちらの書式も受け付ける（NOTICE の書き方はリポジトリごとに違う）
        if grep -qF "${name} ${version} /" "$NOTICE_FILE" || grep -qF "${name} ${version} -" "$NOTICE_FILE"; then
            printf '  OK   %s %s\n' "$name" "$version"
        else
            printf '  NG   %s %s が NOTICE に見当たらない\n' "$name" "$version" >&2
            fail=1
        fi
    done < "$list"
}

check_list NuGet "$work/nuget.tsv"
echo
check_list npm "$work/npm.tsv"

echo
if [ "$fail" -ne 0 ]; then
    echo "NG: NOTICE と実際の依存が食い違っている" >&2
    echo "    NOTICE へ「名称 バージョン / ライセンス / 入手元」の行を足すか、版数を直すこと。" >&2
    echo "    **寛容ライセンス（MIT / BSD 系 / Apache-2.0）以外は採用しないこと**（LICENSING.md）。" >&2
else
    echo "OK: NOTICE の依存一覧は実際の依存と一致している"
fi

exit "$fail"
