$ErrorActionPreference = 'Stop'
$submodulePath = '_reference/Implem.Pleasanter'
$current = (git rev-parse "HEAD:$submodulePath").Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot read submodule pointer.' }
$tags = git ls-remote --tags --refs https://github.com/Implem/Implem.Pleasanter.git
if ($LASTEXITCODE -ne 0) { throw 'Cannot read upstream tags.' }
$releases = foreach ($line in $tags) {
    if ($line -match '^([a-f0-9]{40})\s+refs/tags/(Pleasanter_(\d+\.\d+\.\d+\.\d+))$') {
        [pscustomobject]@{ Sha = $Matches[1]; Tag = $Matches[2]; Version = [version]$Matches[3] }
    }
}
$latest = $releases | Sort-Object Version -Descending | Select-Object -First 1
if (-not $latest) { throw 'No stable Pleasanter tag found.' }
# annotated tag は API で commit に解決する。比較 API で固定済みの先行 commit を巻き戻さない。
$comparisonText = gh api "repos/Implem/Implem.Pleasanter/compare/$($latest.Tag)...$current"
if ($LASTEXITCODE -ne 0) { throw 'Cannot compare upstream commits.' }
$comparison = ($comparisonText -join "`n") | ConvertFrom-Json
$latestSha = $comparison.base_commit.sha
$needsUpdate = $comparison.status -notin @('ahead','identical')
@("needs_update=$($needsUpdate.ToString().ToLowerInvariant())", "latest_tag=$($latest.Tag)", "latest_sha=$latestSha", "current_sha=$current") | Add-Content $env:GITHUB_OUTPUT -Encoding utf8
"Current: $current`n`nLatest stable tag: $($latest.Tag) ($latestSha)`n`nUpdate needed: $needsUpdate" | Add-Content $env:GITHUB_STEP_SUMMARY -Encoding utf8
