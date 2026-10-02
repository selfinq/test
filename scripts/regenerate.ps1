# regenerate: run the patched RecompOne recompiler on generated\ttt_image.bin with tools\ttt_raw.json.
# env: RECOMPONE_DIR, RECOMP_OUT (output dir, default <repo>\generated\recomp)
# NOTE: the current output is ~206 MB of C#; this takes a while and needs several GB of RAM.
$global:ScriptName = 'regenerate.ps1'
. "$PSScriptRoot/common.ps1"
Invoke-Script {
    Set-Location $Repo
    Step 'check inputs'
    Test-Dotnet
    if (-not (Test-Path 'generated/ttt_image.bin')) { Fail 'generated/ttt_image.bin missing: run scripts/import.ps1 first' }
    if (-not (Test-Path 'tools/ttt_raw.json')) { Fail 'tools/ttt_raw.json missing' }
    $dll = Get-RecompilerDll
    if (-not (Test-Path -LiteralPath $dll)) { Fail "recompiler not built ($dll): run scripts/setup.ps1 first" }

    $config = Join-Path $Repo 'tools/ttt_raw.json'
    $defaultOut = [IO.Path]::GetFullPath((Join-Path $Repo 'generated/recomp'))
    if ($RecompOut.TrimEnd('\', '/') -ne $defaultOut.TrimEnd('\', '/')) {
        Step "write config override for output dir $RecompOut"
        $config = Join-Path $Repo 'generated/ttt_raw.override.json'
        $c = Get-Content -Raw 'tools/ttt_raw.json' | ConvertFrom-Json
        $c.image = Join-Path $Repo 'generated/ttt_image.bin'
        $c.game.output = $RecompOut
        $c | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $config
    }

    Step "run recompiler: dotnet recompone.dll $config"
    New-Item -ItemType Directory -Force -Path $RecompOut | Out-Null
    $log = Join-Path $Repo 'generated/regenerate.log'
    & dotnet $dll $config *> $log
    $rc = $LASTEXITCODE
    $lines = Get-Content -LiteralPath $log | Where-Object { $_ -notmatch '^\[Unknown\]' }
    if ($rc -ne 0) { $lines | Select-Object -Last 40 | ForEach-Object { [Console]::Error.WriteLine($_) }; Fail "recompiler exited with $rc; full log: $log" }
    $lines | Select-Object -First 60 | ForEach-Object { Write-Host $_ }

    Step 'check outputs'
    $main = Join-Path $RecompOut 'main.cs'
    if (-not (Test-Path -LiteralPath $main) -or (Get-Item -LiteralPath $main).Length -eq 0) { Fail "no $main produced; see $log" }
    if (-not (Select-String -LiteralPath $log -Pattern 'Recompilation finished' -Quiet)) { Fail "recompiler did not report 'Recompilation finished'; see $log" }
    Ok "$(@(Get-ChildItem -LiteralPath $RecompOut).Count) file(s) in $RecompOut; main.cs $((Get-Item -LiteralPath $main).Length) bytes"
    Write-Host "[Unknown] decode warnings in log: $(@(Select-String -LiteralPath $log -Pattern '^\[Unknown\]').Count)"
    Write-Host ''; Write-Host 'regenerate finished OK'
}
