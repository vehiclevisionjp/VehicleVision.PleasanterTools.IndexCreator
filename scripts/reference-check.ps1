# 参照用サブモジュール `_reference/Implem.Pleasanter` の固定位置が、上流の main の先頭と一致しているかを見る。
# 追従先は main の先頭（2026-10-19 に、リリースタグへの固定から変えた。_reference/README.md）。
# main はリリース前の変更を含むので、「どの版か」はコミット SHA と <Version> で言う。
# リリースタグ（Pleasanter_*）と <Version> は表示のためだけに読む。取れなくても止めない。
# `Release_*` 系のタグは 2020 年で更新が止まっているので使わない。
# 出力: needs_update / latest_ref / latest_sha / latest_tag / latest_version / current_tag / current_sha
# 試験用の上書き（本番では設定しない）: REFERENCE_UPSTREAM_URL / REFERENCE_UPSTREAM_RAW
$ErrorActionPreference = 'Stop'
$submodulePath = '_reference/Implem.Pleasanter'
$upstream = if ($env:REFERENCE_UPSTREAM_URL) { $env:REFERENCE_UPSTREAM_URL } else { 'https://github.com/Implem/Implem.Pleasanter.git' }
$raw = if ($env:REFERENCE_UPSTREAM_RAW) { $env:REFERENCE_UPSTREAM_RAW } else { 'https://raw.githubusercontent.com/Implem/Implem.Pleasanter' }
$trackRef = 'main'

$current = (git rev-parse "HEAD:$submodulePath").Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot read submodule pointer.' }

$mainLines = git ls-remote $upstream "refs/heads/$trackRef"
if ($LASTEXITCODE -ne 0) { throw "Cannot read upstream $trackRef." }
$latestSha = ''
foreach ($line in $mainLines) {
    if ($line -match "^([0-9a-f]{40})\s+refs/heads/$trackRef$") { $latestSha = $Matches[1] }
}
if ($latestSha -notmatch '^[0-9a-f]{40}$') { throw "Cannot find upstream $trackRef head." }

# リリースタグ（表示のため）。注釈付きタグは ^{} の行が指し先のコミットを持つ
$releases = @()
$tagLines = git ls-remote --tags $upstream 'refs/tags/Pleasanter_*'
if ($LASTEXITCODE -eq 0) {
    $releases = @(foreach ($line in $tagLines) {
        if ($line -match '^([0-9a-f]{40})\s+refs/tags/(Pleasanter_(\d+(?:\.\d+)*))\^\{\}$') {
            [pscustomobject]@{ Sha = $Matches[1]; Tag = $Matches[2]; Version = [version]$Matches[3] }
        }
    })
}
function Get-TagOf([string]$sha) {
    $hit = $releases | Where-Object { $_.Sha -eq $sha } | Sort-Object Version -Descending | Select-Object -First 1
    if ($hit) { $hit.Tag } else { '' }
}
$latestTag = Get-TagOf $latestSha
$currentTag = Get-TagOf $current

# <Version>（表示のため。取れなくても止めない）
$latestVersion = ''
try {
    $csproj = Invoke-RestMethod -Uri "$raw/$latestSha/Implem.Pleasanter/Implem.Pleasanter.csproj" -TimeoutSec 20
    if ($csproj -match '<Version>([^<]+)') { $latestVersion = $Matches[1] }
} catch { }

$needsUpdate = ($current -ne $latestSha).ToString().ToLowerInvariant()
$outputs = @(
    "latest_ref=$trackRef", "latest_sha=$latestSha", "latest_tag=$latestTag", "latest_version=$latestVersion",
    "current_tag=$currentTag", "current_sha=$current", "needs_update=$needsUpdate")
if ($env:GITHUB_OUTPUT) { $outputs | Add-Content $env:GITHUB_OUTPUT -Encoding utf8 } else { $outputs }
if ($env:GITHUB_STEP_SUMMARY) {
    $currentLabel = if ($currentTag) { $currentTag } else { '（タグ無し）' }
    $versionLabel = if ($latestVersion) { " (Version $latestVersion)" } else { '' }
    "固定位置: ``$current`` ($currentLabel)`n`n上流の $trackRef の先頭: ``$latestSha``$versionLabel`n`n更新が必要: $needsUpdate" |
        Add-Content $env:GITHUB_STEP_SUMMARY -Encoding utf8
}
