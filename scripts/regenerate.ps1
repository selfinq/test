# regenerate: run the patched RecompOne recompiler on the decompressed image with a raw-mode config.
# env: RECOMPONE_DIR   RecompOne checkout (default <repo>\..\work\recompone)
#      RECOMP_CONFIG   config to use (default <repo>\tools\ttt_raw.json; e.g. tools\ttt_raw_a01.json)
#      RECOMP_OUT      output dir; if set and different from the config's game.output, a copy of the config
#                      with that output dir is written to generated\ttt_raw.override.json and used
# NOTE: the default config's output is ~206 MB of C#; this takes a while and needs several GB of RAM.
$global:ScriptName = 'regenerate.ps1'
. "$PSScriptRoot/common.ps1"
Invoke-Script {
    Set-Location $Repo
    Step 'check inputs'
    Test-Dotnet
    $config = [IO.Path]::GetFullPath((Get-EnvOr 'RECOMP_CONFIG' (Join-Path $Repo 'tools/ttt_raw.json')))
    if (-not (Test-Path -LiteralPath $config)) { Fail "config not found: $config" }
    $dll = Get-RecompilerDll
    if (-not (Test-Path -LiteralPath $dll)) { Fail "recompiler not built ($dll): run scripts/setup.ps1 first" }
    $cfg = Get-Content -Raw -LiteralPath $config | ConvertFrom-Json
    $cfgDir = Split-Path $config -Parent
    $image = [IO.Path]::GetFullPath((Join-Path $cfgDir $cfg.image))
    $cfgOut = [IO.Path]::GetFullPath((Join-Path $cfgDir $cfg.game.output))
    if (-not (Test-Path -LiteralPath $image)) { Fail "image $image missing: run scripts/import.ps1 first" }
    $outDir = if ($RecompOutUser) { [IO.Path]::GetFullPath($RecompOutUser) } else { $cfgOut }

    if ($outDir.TrimEnd('\', '/') -ne $cfgOut.TrimEnd('\', '/')) {
        Step "write config override for output dir $outDir"
        $override = Join-Path $Repo 'generated/ttt_raw.override.json'
        $cfg.image = $image
        $cfg.game.output = $outDir
        $cfg | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $override
        $config = $override
    }

    Step "run recompiler: dotnet recompone.dll $config"
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $log = Join-Path $Repo 'generated/regenerate.log'
    & dotnet $dll $config *> $log
    $rc = $LASTEXITCODE
    $lines = Get-Content -LiteralPath $log | Where-Object { $_ -notmatch '^\[Unknown\]' }
    if ($rc -ne 0) { $lines | Select-Object -Last 40 | ForEach-Object { [Console]::Error.WriteLine($_) }; Fail "recompiler exited with $rc; full log: $log" }
    $lines | Select-Object -First 60 | ForEach-Object { Write-Host $_ }

    Step 'check outputs'
    $main = Join-Path $outDir 'main.cs'
    if (-not (Test-Path -LiteralPath $main) -or (Get-Item -LiteralPath $main).Length -eq 0) { Fail "no $main produced; see $log" }
    if (-not (Select-String -LiteralPath $log -Pattern 'Recompilation finished' -Quiet)) { Fail "recompiler did not report 'Recompilation finished'; see $log" }
    Ok "$(@(Get-ChildItem -LiteralPath $outDir).Count) file(s) in $outDir; main.cs $((Get-Item -LiteralPath $main).Length) bytes"
    Write-Host "[Unknown] decode warnings in log: $(@(Select-String -LiteralPath $log -Pattern '^\[Unknown\]').Count)"
    Write-Host ''; Write-Host 'regenerate finished OK'
}
