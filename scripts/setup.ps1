# setup: check toolchain, clone RecompOne at the pinned commit, apply patches/*.patch in order, build the recompiler.
# env: RECOMPONE_DIR (default <repo>\..\work\recompone), RECOMPONE_URL
# usage: pwsh scripts/setup.ps1 [-SkipBuild]      (Windows PowerShell: powershell -File scripts\setup.ps1)
param([switch]$SkipBuild)
$global:ScriptName = 'setup.ps1'
. "$PSScriptRoot/common.ps1"
Invoke-Script {
    Step "check toolchain (git, python, dotnet >= $($Pins['DOTNET_MIN_MAJOR']))"
    Need-Cmd 'git' 'Install git.'
    Find-Python
    Test-Dotnet

    $url = Get-EnvOr 'RECOMPONE_URL' $Pins['RECOMPONE_URL']
    $pin = $Pins['RECOMPONE_COMMIT']
    Step "obtain RecompOne at $pin in $RecomponeDir"
    if (-not (Test-Path -LiteralPath $RecomponeDir)) {
        New-Item -ItemType Directory -Force -Path (Split-Path $RecomponeDir -Parent) | Out-Null
        Invoke-Native git @('clone', $url, $RecomponeDir)
    }
    if ((Get-NativeExit git @('-C', $RecomponeDir, 'rev-parse', '--git-dir')) -ne 0) { Fail "$RecomponeDir exists but is not a git checkout" }
    $head = (& git -C $RecomponeDir rev-parse HEAD).Trim()
    if ($head -ne $pin) {
        Write-Host "HEAD is $head, pin is $pin"
        $dirty = & git -C $RecomponeDir status --porcelain --untracked-files=no
        if ($dirty) { Fail 'checkout is at the wrong commit AND has local modifications; refusing to switch. Use a clean checkout or set RECOMPONE_DIR.' }
        if ((Get-NativeExit git @('-C', $RecomponeDir, 'cat-file', '-e', "$pin^{commit}")) -ne 0) {
            Invoke-Native git @('-C', $RecomponeDir, 'fetch', 'origin', $pin)
        }
        Invoke-Native git @('-C', $RecomponeDir, 'checkout', '--detach', $pin)
    }
    Ok "RecompOne HEAD = $((& git -C $RecomponeDir rev-parse HEAD).Trim())"

    Step 'apply patches/*.patch in order'
    $patches = @(Get-ChildItem -LiteralPath (Join-Path $Repo 'patches') -Filter '*.patch' | Sort-Object Name)
    if ($patches.Count -eq 0) { Fail "no patches found in $(Join-Path $Repo 'patches')" }
    foreach ($p in $patches) {
        $global:CurrentStep = "apply $($p.Name)"
        if ((Get-NativeExit git @('-C', $RecomponeDir, 'apply', '--check', $p.FullName)) -eq 0) {
            Invoke-Native git @('-C', $RecomponeDir, 'apply', $p.FullName)
            Ok "applied $($p.Name)"
        } elseif ((Get-NativeExit git @('-C', $RecomponeDir, 'apply', '--reverse', '--check', $p.FullName)) -eq 0) {
            Ok "already applied: $($p.Name)"
        } else {
            Fail "$($p.Name) neither applies nor is already applied to $RecomponeDir (is the checkout modified by something else?)"
        }
    }

    if ($SkipBuild) {
        Step 'build skipped (-SkipBuild)'
    } else {
        Step 'build RecompOne.Recompiler (Release)'
        Invoke-Native dotnet @('build', (Join-Path $RecomponeDir 'RecompOne.Recompiler/RecompOne.Recompiler.csproj'), '-c', 'Release', '--nologo')
        if (-not (Test-Path -LiteralPath (Get-RecompilerDll))) { Fail "build reported success but $(Get-RecompilerDll) does not exist" }
        Ok "recompiler: $(Get-RecompilerDll)"
    }
    Write-Host ''; Write-Host 'setup finished OK'
}
