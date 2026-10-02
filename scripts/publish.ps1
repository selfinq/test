# publish: self-contained publish of the host for a runtime id (default win-x64; also linux-x64).
# usage: pwsh scripts/publish.ps1 [-Rid win-x64|linux-x64]      env: EXTRA_MSBUILD
param([ValidateSet('win-x64', 'linux-x64')][string]$Rid = 'win-x64')
$global:ScriptName = 'publish.ps1'
. "$PSScriptRoot/common.ps1"
Invoke-Script {
    Set-Location $Repo
    Step "check dotnet >= $($Pins['DOTNET_MIN_MAJOR'])"
    Test-Dotnet
    Step "dotnet publish -c Release -r $Rid --self-contained"
    Invoke-Native dotnet (@('publish', $HostProject, '-c', 'Release', '-r', $Rid, '--self-contained', '--nologo') + $ExtraMsbuild)
    $out = Join-Path $Repo "host/TttHost/bin/Release/net10.0/$Rid/publish"
    Step "check publish output $out"
    $exe = if ($Rid -like 'win-*') { 'TttRecompHost.exe' } else { 'TttRecompHost' }
    if (-not (Test-Path -LiteralPath (Join-Path $out $exe))) { Fail "$out\$exe missing" }
    if (-not (Test-Path -LiteralPath (Join-Path $out 'tektagt_manifest.json'))) { Fail "$out\tektagt_manifest.json missing" }
    $files = @(Get-ChildItem -LiteralPath $out -Recurse -File)
    $mb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
    Ok "$out : $($files.Count) files, $mb MB"
    if ($Rid -like 'win-*' -and [Environment]::OSVersion.Platform -ne 'Win32NT') { Write-Host 'NOTE: this Windows build was produced on a non-Windows OS and has NOT been executed on Windows.' }
    Write-Host ''; Write-Host 'publish finished OK'
}
