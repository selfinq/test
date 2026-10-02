# import: verify ROM zip (by content) -> build regions -> decompress boot image -> check image hash.
# env: ROM_ZIP (default <repo>\input\roms\tektagt.zip). Never downloads anything.
$global:ScriptName = 'import.ps1'
. "$PSScriptRoot/common.ps1"
Invoke-Script {
    Set-Location $Repo
    Step 'locate python and ROM zip'
    Find-Python
    if (-not (Test-Path -LiteralPath $RomZip)) {
        Fail "ROM zip not found: $RomZip (supply your own tektagt.zip; set ROM_ZIP or put it at input\roms\tektagt.zip; this project never downloads ROMs)"
    }
    Write-Host "ROM zip: $RomZip"
    New-Item -ItemType Directory -Force -Path 'generated' | Out-Null

    Step 'verify ROM set by content (tools/verify_romset.py)'
    $out = & $PythonExe @PythonPre tools/verify_romset.py $RomZip --json generated/romset_verify.json
    $rc = $LASTEXITCODE
    $out | Set-Content -Encoding utf8 generated/romset_verify.stdout.json
    if ($rc -ne 0) { Fail "ROM set incomplete or mismatched (exit $rc); see generated/romset_verify.stdout.json" }
    $r = Get-Content -Raw generated/romset_verify.json | ConvertFrom-Json
    Write-Host "complete = $($r.complete) | ok members = $(@($r.ok).Count)"

    Step 'build memory regions (tools/build_regions.py)'
    Invoke-Python @('tools/build_regions.py', $RomZip, '--out', 'generated/regions')

    Step 'decompress boot image (tools/ttt_decompress.py)'
    $srcOff = '0x28000'
    $expPath = 'tests/expected_image.json'
    $exp = $null
    if (Test-Path $expPath) { $exp = Get-Content -Raw $expPath | ConvertFrom-Json; $srcOff = $exp.source_rom_offset }
    Write-Host "source ROM offset: $srcOff"
    Invoke-Python @('tools/ttt_decompress.py', 'generated/regions/maincpu_rom.bin', $srcOff, 'generated/ttt_image.bin')

    Step 'check image against tests/expected_image.json (MAME RAM hash)'
    if ($exp) {
        $got = Get-Sha256 'generated/ttt_image.bin'
        if ($got -ne $exp.image_sha256) { Fail "generated/ttt_image.bin sha256 $got != expected $($exp.image_sha256)" }
        Ok "image sha256 matches expected ($got)"
    } else {
        Write-Host 'tests/expected_image.json missing: hash check skipped'
    }
    Write-Host ''; Write-Host 'import finished OK'
}
