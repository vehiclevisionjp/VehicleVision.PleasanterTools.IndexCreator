param([string]$Output = 'artifacts/package')
$ErrorActionPreference = 'Stop'
foreach ($rid in @('win-x86','win-x64','linux-x64','linux-arm64')) {
    $folder = Join-Path $Output "$rid/IndexCreator"
    dotnet publish src/IndexCreator/IndexCreator.csproj -c Release -r $rid --self-contained false --no-restore -p:DebugType=none -p:DebugSymbols=false -o $folder
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item LICENSE, NOTICE, ThirdPartyNotices.txt -Destination $folder
    Compress-Archive -Path $folder -DestinationPath (Join-Path $Output "VehicleVision.PleasanterTools.IndexCreator-$rid.zip") -Force
    $hash = Get-FileHash (Join-Path $Output "VehicleVision.PleasanterTools.IndexCreator-$rid.zip") -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  VehicleVision.PleasanterTools.IndexCreator-$rid.zip" | Set-Content (Join-Path $Output "VehicleVision.PleasanterTools.IndexCreator-$rid.zip.sha256") -Encoding utf8
}
