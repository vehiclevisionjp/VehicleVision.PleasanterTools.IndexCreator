$ErrorActionPreference = 'Stop'
$branch = "chore/reference-$($env:LATEST_TAG)"
$existingText = gh pr list --base develop --head $branch --state open --json number
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect existing PRs.' }
if (($existingText | ConvertFrom-Json).Count -gt 0) { Write-Output 'An update PR already exists.'; exit 0 }
git switch -c $branch
if ($LASTEXITCODE -ne 0) { throw 'Cannot create update branch.' }
git update-index --cacheinfo "160000,$($env:LATEST_SHA),_reference/Implem.Pleasanter"
if ($LASTEXITCODE -ne 0) { throw 'Cannot update submodule pointer.' }
git config user.name 'IndexCreator automation'
git config user.email 'indexcreator-automation@users.noreply.github.com'
git commit -m "Pleasanter 参照を $($env:LATEST_TAG) に更新する"
if ($LASTEXITCODE -ne 0) { throw 'Cannot commit update.' }
$issueBody = "参照を $($env:CURRENT_SHA) から $($env:LATEST_SHA) ($($env:LATEST_TAG)) に更新する。本体の引数・設定・DB 定義の互換性を確認し、設計資料と検証結果を更新する。自動マージは行わない。"
$issuePath = Join-Path $env:RUNNER_TEMP 'reference-issue.md'
$issueBody | Set-Content $issuePath -Encoding utf8
$issueUrl = gh issue create --title "Pleasanter 参照を $($env:LATEST_TAG) に更新する" --body-file $issuePath
if ($LASTEXITCODE -ne 0) { throw 'Cannot create update issue.' }
$number = ($issueUrl -split '/')[-1]
git push origin $branch
if ($LASTEXITCODE -ne 0) { throw 'Cannot push update branch.' }
$bodyPath = Join-Path $env:RUNNER_TEMP 'reference-pr.md'
"Pleasanter 参照サブモジュールを $($env:LATEST_TAG) に更新する。実装・配布物には本体を含めない。互換性と DB 実機検証、資料更新をレビューで確認する。`n`nCloses #$number" | Set-Content $bodyPath -Encoding utf8
gh pr create --base develop --head $branch --draft --title "WIP: Pleasanter 参照を $($env:LATEST_TAG) に更新する" --body-file $bodyPath
if ($LASTEXITCODE -ne 0) { throw 'Cannot create update PR.' }
