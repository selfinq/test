# run: run the host (TttRecompHost). TODAY this prints the status and verifies the ROM zip; it does NOT run the game.
# usage: pwsh scripts/run.ps1 [host args...]   e.g. pwsh scripts/run.ps1 --rom input/roms/tektagt.zip
# host exit codes: 0 ROM set verified | 1 ROM incomplete/mismatched | 2 usage/no ROM/unsafe archive
$global:ScriptName = 'run.ps1'
. "$PSScriptRoot/common.ps1"
$global:HostArgs = $args
Invoke-Script {
    Set-Location $Repo
    $dll = Join-Path $Repo 'host/TttHost/bin/Release/net10.0/TttRecompHost.dll'
    Step 'check dotnet'
    Test-Dotnet
    if (-not (Test-Path -LiteralPath $dll)) {
        Step 'host not built: building it first'
        & (Join-Path $PSScriptRoot 'build.ps1')
        if ($LASTEXITCODE -ne 0) { Fail 'build.ps1 failed' }
    }
    Step "run TttRecompHost $($HostArgs -join ' ')"
    & dotnet $dll @HostArgs
    $global:HostRc = $LASTEXITCODE
}
Write-Host ''
Write-Host "host exit code: $HostRc (0 = ROM set verified, 1 = ROM incomplete/mismatched, 2 = usage / no ROM / unsafe archive)"
exit $HostRc
