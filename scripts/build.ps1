# build: build src/System12.Runtime (if present) and host/TttHost (Release).
# usage: pwsh scripts/build.ps1 [-WithRecomp] [-AllowNoRuntime]
#   -WithRecomp       also compile generated\recomp\*.cs into the host (206 MB; Roslyn ran out of memory once, see docs/BUILD.md)
#   -AllowNoRuntime   if System12.Runtime fails to build, build the host without it (the status line says so)
# env: EXTRA_MSBUILD="-p:S12ExcludeFolders=Gpu;Sound" (extra msbuild args for every dotnet call)
param([switch]$WithRecomp, [switch]$AllowNoRuntime)
$global:ScriptName = 'build.ps1'
. "$PSScriptRoot/common.ps1"
Invoke-Script {
    Set-Location $Repo
    $hostExtra = @()
    Step "check dotnet >= $($Pins['DOTNET_MIN_MAJOR'])"
    Test-Dotnet

    if (Test-Path -LiteralPath $RuntimeProject) {
        Step 'build src/System12.Runtime (Release)'
        & dotnet build $RuntimeProject -c Release --nologo @ExtraMsbuild
        if ($LASTEXITCODE -ne 0) {
            if ($AllowNoRuntime) {
                [Console]::Error.WriteLine('WARNING: System12.Runtime failed to build; continuing WITHOUT it (-AllowNoRuntime)')
                $hostExtra += '-p:UseSystem12Runtime=false'
            } else {
                Fail "System12.Runtime does not build (exit $LASTEXITCODE). Fix it, exclude broken folders (EXTRA_MSBUILD='-p:S12ExcludeFolders=Gpu;Sound'), or pass -AllowNoRuntime."
            }
        }
    } else {
        Step "System12.Runtime project not present ($RuntimeProject): host is built without it"
    }

    if ($WithRecomp) {
        if (-not (Test-Path -LiteralPath $RecompOut)) { Fail "-WithRecomp but $RecompOut does not exist: run scripts/regenerate.ps1 first" }
        $hostExtra += "-p:RecompDir=$RecompOut"
        Write-Host "including generated code from $RecompOut"
    }

    Step 'build host/TttHost (Release)'
    Invoke-Native dotnet (@('build', $HostProject, '-c', 'Release', '--nologo') + $ExtraMsbuild + $hostExtra)
    $dll = Join-Path $Repo 'host/TttHost/bin/Release/net10.0/TttRecompHost.dll'
    if (-not (Test-Path -LiteralPath $dll)) { Fail "build reported success but $dll does not exist" }
    Ok "host: $dll"
    Write-Host ''; Write-Host 'build finished OK'
}
