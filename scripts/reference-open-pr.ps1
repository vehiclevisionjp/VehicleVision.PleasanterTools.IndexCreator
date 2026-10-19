# 参照用サブモジュールのポインタを、上流の main の先頭へ進め、Issue と Draft PR を出す。
# main へ追従する 1 本の PR にする。開いている PR があれば、同じブランチへコミットを足して本文を書き換える。
# 無ければ、Issue → ブランチ → Draft PR を出す。
# サブモジュールは clone しない。索引の gitlink（160000）を git update-index --cacheinfo で書き換える。
# 版を書いてあるドキュメントは自動で書き換えない（確認していない版を根拠にしないため）。PR 本文で人に確認させる。
# 要る環境変数: GH_TOKEN（push と PR。App のトークン）/ ISSUE_GH_TOKEN（Issue のみ。GITHUB_TOKEN）/
#   LATEST_SHA / CURRENT_SHA（必須）/ LATEST_REF / LATEST_TAG / LATEST_VERSION / CURRENT_TAG / BASE_BRANCH
$ErrorActionPreference = 'Stop'
if (-not $env:LATEST_SHA -or -not $env:CURRENT_SHA) { throw 'LATEST_SHA and CURRENT_SHA are required.' }
$latestSha = $env:LATEST_SHA
$latestRef = if ($env:LATEST_REF) { $env:LATEST_REF } else { 'main' }
$baseBranch = if ($env:BASE_BRANCH) { $env:BASE_BRANCH } else { 'develop' }
$submodulePath = '_reference/Implem.Pleasanter'
$short = $latestSha.Substring(0, 8)
$branch = 'chore/reference-submodule-main'
$currentLabel = if ($env:CURRENT_TAG) { $env:CURRENT_TAG } else { $env:CURRENT_SHA.Substring(0, 8) }
$currentNote = if ($env:CURRENT_TAG) { $env:CURRENT_TAG } else { '（タグ無し）' }
$latestLabel = "$latestRef の先頭 ``$short``"
if ($env:LATEST_VERSION) { $latestLabel += "（Version $($env:LATEST_VERSION)）" }
if ($env:LATEST_TAG) { $latestLabel += "、``$($env:LATEST_TAG)`` と同じコミット" }
$latestNote = ''
if ($env:LATEST_VERSION) { $latestNote += "Version $($env:LATEST_VERSION)" }
if ($env:LATEST_TAG) { $latestNote += "、$($env:LATEST_TAG) と同じコミット" }
$tempDir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }

function Write-Summary([string]$text) {
    if ($env:GITHUB_STEP_SUMMARY) { $text | Add-Content $env:GITHUB_STEP_SUMMARY -Encoding utf8 } else { Write-Output $text }
}
function Get-PrBody([string]$issueNumber) {
@"
## 概要

``$submodulePath`` のポインタを ``$currentLabel`` から $latestLabel（``$latestSha``）へ進める。

日次の検査（``.github/workflows/reference-submodule-update.yml``）が、上流の ``$latestRef`` に新しいコミットを検出して自動で出した。

**この PR は、上流の ``$latestRef`` へ追従する 1 本の PR。** 未マージのうちに ``$latestRef`` が進んだら、同じ PR へコミットを足してポインタを最新へ進める（この本文の SHA もそのとき書き換わる）。

## ⚠️ マージ前にやること

**版を書いてあるドキュメントは自動で書き換えていない。** 「この版の実ソースを根拠にしている」と書いた文書の版だけ機械的に上げると、確認していない版を根拠にしたことになる。次を人が確認して揃えること。
**``$latestRef`` はリリース前の変更を含む。** 文書には、確認した**コミットと ``<Version>``** を書くこと（リリース版との対応は ``Pleasanter_*`` タグで確かめる）。

- [ ] ``_reference/README.md`` の「固定位置」
- [ ] 本体の引数・設定・DB 定義の互換性（設計資料と検証結果の、版を書いた箇所）
- [ ] ``README.md`` や開発ガイドの技術スタックが指す版（対象フレームワークの変化も見る）

GitHub App の短期トークンで作成した PR のため、通常の CI が起動する。

## 制約の確認

- **コードの取り込みは行っていない。** 参照専用のポインタ更新のみ
- サブモジュールはビルド対象・配布物に含まれない

Closes #$issueNumber
"@
}
function Get-CommitMessage {
@"
参照用サブモジュール Implem.Pleasanter を $latestRef の先頭（$short）へ進める

固定位置が $currentLabel のままで、上流の $latestRef の先頭より遅れていた。
参照専用のサブモジュールが古いと、確認結果が現行の Pleasanter と食い違う。

検証したこと: 日次のワークフローが、上流の $latestRef の先頭（$latestSha）と固定位置が違うことを確認した。
サブモジュールはビルド対象外のため本体のビルドには影響しない。コードの取り込みは行っていない。
版を書いてあるドキュメントは自動で書き換えていない（PR 本文を参照）。
"@
}
function Set-Pointer([string]$message) {
    git update-index --cacheinfo "160000,$latestSha,$submodulePath"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot update submodule pointer.' }
    git commit -m $message
    if ($LASTEXITCODE -ne 0) { throw 'Cannot commit update.' }
}
git config user.name 'github-actions[bot]'
git config user.email '41898282+github-actions[bot]@users.noreply.github.com'

# ---- 開いている追従 PR があれば、同じブランチへコミットを足す ----
$existingText = gh pr list --base $baseBranch --head $branch --state open --json number,body
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect existing PRs.' }
$existing = @(($existingText -join "`n") | ConvertFrom-Json) | Select-Object -First 1
if ($existing) {
    git fetch origin $branch
    if ($LASTEXITCODE -ne 0) { throw 'Cannot fetch update branch.' }
    $remoteSha = ''
    foreach ($line in (git ls-tree "origin/$branch" $submodulePath)) {
        if ($line -match '^\d+\s+commit\s+([0-9a-f]{40})\s') { $remoteSha = $Matches[1] }
    }
    if ($remoteSha -eq $latestSha) {
        Write-Summary "## 既に PR が出ています`n`n$latestRef の先頭（``$short``）への更新は #$($existing.number) で進行中です。"
        exit 0
    }
    if ($existing.body -notmatch 'Closes #(\d+)') { throw "PR #$($existing.number) の本文に 'Closes #N' が見つからない。Issue 番号を引き継げない。" }
    $issueNumber = $Matches[1]
    git switch -C $branch "origin/$branch"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot switch to update branch.' }
    Set-Pointer (Get-CommitMessage)
    git push origin $branch
    if ($LASTEXITCODE -ne 0) { throw 'Cannot push update branch.' }
    $bodyPath = Join-Path $tempDir 'reference-pr.md'
    Get-PrBody $issueNumber | Set-Content $bodyPath -Encoding utf8
    gh pr edit $existing.number --body-file $bodyPath
    if ($LASTEXITCODE -ne 0) { throw 'Cannot update PR body.' }
    Write-Summary "## 開いている PR を更新しました`n`n- #$($existing.number): ポインタを $latestLabel へ進めた`n- Issue #$issueNumber"
    exit 0
}

# 閉じられた PR のブランチが残っていると、同じ名前では push できない。短い SHA を足して避ける
git ls-remote --exit-code --heads origin $branch *> $null
if ($LASTEXITCODE -eq 0) { $branch = "$branch-$short" }

$issueBody = @"
## 背景

日次の検査（``.github/workflows/reference-submodule-update.yml``）が、``$submodulePath`` の固定位置が上流の ``$latestRef`` の先頭より遅れていることを検出した。

| | コミット | 備考 |
|---|---|---|
| いま | ``$($env:CURRENT_SHA)`` | $currentNote |
| 上流の $latestRef | ``$latestSha`` | $latestNote |

（検出した時点の値。追従 PR が更新されても、この表は書き換わらない。最新の値は PR の本文を見る。）

## 対応内容

- ポインタを上流の ``$latestRef`` の先頭へ進める（自動 PR を用意した。``$latestRef`` が進めば同じ PR へ足す）
- **版を書いてあるドキュメントを、そのコミットで確認し直して揃える**（人の作業）

## 制約

- **コードの取り込みは行わない。** 参照ポインタの更新のみ
"@
$issuePath = Join-Path $tempDir 'reference-issue.md'
$issueBody | Set-Content $issuePath -Encoding utf8
# Issue の作成だけは ISSUE_GH_TOKEN（GITHUB_TOKEN）で行う。App に Issues の権限を足さずに済ませるため
$appToken = $env:GH_TOKEN
if ($env:ISSUE_GH_TOKEN) { $env:GH_TOKEN = $env:ISSUE_GH_TOKEN }
$issueUrl = gh issue create --title "参照用サブモジュール Implem.Pleasanter を上流の $latestRef の先頭へ進める" --body-file $issuePath
$issueExit = $LASTEXITCODE
$env:GH_TOKEN = $appToken
if ($issueExit -ne 0) { throw 'Cannot create update issue.' }
$issueNumber = ($issueUrl -split '/')[-1]

git switch -c $branch
if ($LASTEXITCODE -ne 0) { throw 'Cannot create update branch.' }
Set-Pointer "$(Get-CommitMessage)`n`nCloses #$issueNumber"
git push origin $branch
if ($LASTEXITCODE -ne 0) { throw 'Cannot push update branch.' }
$bodyPath = Join-Path $tempDir 'reference-pr.md'
Get-PrBody $issueNumber | Set-Content $bodyPath -Encoding utf8
# Draft ＋ WIP: で出す。Draft 解除＝レビュー可能、WIP: 除去＝マージ可能の 2 段のゲート
gh pr create --base $baseBranch --head $branch --draft --title "WIP: 参照用サブモジュール Implem.Pleasanter を上流の $latestRef の先頭へ進める" --body-file $bodyPath
if ($LASTEXITCODE -ne 0) { throw 'Cannot create update PR.' }
Write-Summary "## 更新の PR を出しました`n`n- ``$currentLabel`` → $latestLabel`n- Issue #$issueNumber"
