param([ValidateSet('win-x64','linux-x64')][string]$Rid = $(if ($IsWindows) { 'win-x64' } else { 'linux-x64' }), [string]$PackageDirectory = 'artifacts/package')
$ErrorActionPreference = 'Stop'
$repoPath = (Get-Location).Path
$zipPath = Join-Path $repoPath (Join-Path $PackageDirectory "IndexCreator-$Rid.zip")
if (-not (Test-Path -LiteralPath $zipPath)) { throw 'Build the distribution ZIP before running App Service smoke checks.' }
$smokeHome = Join-Path $repoPath ('temp/appservice-' + [guid]::NewGuid().ToString('N'))
$wwwroot = Join-Path $smokeHome 'site/wwwroot'
$pleasanter = Join-Path $wwwroot 'Implem.Pleasanter'
$parameters = Join-Path $pleasanter 'App_Data/Parameters'
$outside = Join-Path $smokeHome 'data/console working directory'
New-Item -ItemType Directory -Force $parameters, $outside, (Join-Path $wwwroot 'Implem.CodeDefiner') | Out-Null
Expand-Archive -LiteralPath $zipPath -DestinationPath $wwwroot
Copy-Item examples/sites.json (Join-Path $smokeHome 'sites.json')
@{ Name = 'Implem.Pleasanter'; EnvironmentName = 'PLEASANTER' } | ConvertTo-Json | Set-Content (Join-Path $parameters 'Service.json') -Encoding utf8
@{ Dbms = 'SQLServer'; OwnerConnectionString = ''; DisableIndexChangeDetection = $true; SqlCommandTimeOut = 3 } | ConvertTo-Json | Set-Content (Join-Path $parameters 'Rds.json') -Encoding utf8
$app = Join-Path $wwwroot 'IndexCreator/IndexCreator.dll'
$sites = Join-Path $smokeHome 'sites.json'
$names = @('WEBSITE_SITE_NAME','WEBSITE_INSTANCE_ID','DOTNET_ENVIRONMENT','INDEXCREATOR_DBMS','INDEXCREATOR_CONNECTION_STRING','PLEASANTER_OwnerConnectionString')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
$count = 0
function Invoke-Checked([string[]]$Arguments, [int]$ExpectedExit = 0, [string]$ExpectedText) {
    # stderr も文字列として捕捉し、文字コードに依存する非 ASCII の出力がないことを確認する。
    $lines = & dotnet $app @Arguments 2>&1
    $code = $LASTEXITCODE
    $text = $lines -join "`n"
    if ($code -ne $ExpectedExit) { throw "Unexpected CLI exit code: $code. $text" }
    if ($text -match '[^\x00-\x7F]') { throw 'Non-ASCII console output detected.' }
    if ($text -match 'PRIVATE_TEST_MARKER_DO_NOT_LOG') { throw 'A connection secret leaked into console output.' }
    if ($ExpectedText -and $text -notmatch $ExpectedText) { throw "Missing expected CLI message: $ExpectedText" }
    $script:count++
}
try {
    $env:WEBSITE_SITE_NAME = 'IndexCreator-local-appservice-test'
    $env:WEBSITE_INSTANCE_ID = 'local-test-instance'
    $env:DOTNET_ENVIRONMENT = 'Production'
    $env:INDEXCREATOR_DBMS = ''
    $env:INDEXCREATOR_CONNECTION_STRING = ''
    $env:PLEASANTER_OwnerConnectionString = ''
    Push-Location $outside
    try {
        Invoke-Checked -Arguments @('plan','--sites',$sites) -ExpectedText 'Desired indexes: 2'
        Invoke-Checked -Arguments @('_rds','/p',$pleasanter,'/c','--sites',$sites) -ExpectedText 'Desired indexes: 2'
        Invoke-Checked -Arguments @('_rds','-p',$pleasanter,'/c','--sites',$sites) -ExpectedText 'Desired indexes: 2'
        $env:INDEXCREATOR_DBMS = 'PostgreSQL'
        Invoke-Checked -Arguments @('plan','--sites',$sites) -ExpectedText 'Desired indexes: 1'
        $env:INDEXCREATOR_DBMS = 'SQLServer'
        $env:PLEASANTER_OwnerConnectionString = 'Server=127.0.0.1,1;Database=Unavailable;User ID=Smoke;Password=PRIVATE_TEST_MARKER_DO_NOT_LOG;Connect Timeout=1;Encrypt=true;TrustServerCertificate=true'
        Invoke-Checked -Arguments @('plan','/p',$pleasanter) -ExpectedExit 3 -ExpectedText 'Database operation failed'
        # wwwroot 直下に本体ファイルを置く配置は /p wwwroot で指定する。
        Copy-Item (Join-Path $pleasanter 'App_Data') -Destination $wwwroot -Recurse
        Invoke-Checked -Arguments @('_rds','/p',$wwwroot,'/c','--sites',$sites) -ExpectedText 'Desired indexes: 2'
    } finally { Pop-Location }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
}
Write-Output "App Service layout smoke checks passed ($Rid): $count."
Write-Output "Fixture retained under: $smokeHome"
