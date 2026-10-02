# Shared helpers for scripts/*.ps1. Dot-source it; do not execute it.
# Works in Windows PowerShell 5.1 and PowerShell 7+. Every script stops at the first error and prints the failing step.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

if (-not (Test-Path variable:global:ScriptName)) { $global:ScriptName = 'script.ps1' }
$global:Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$global:CurrentStep = '(before first step)'
$global:StepNo = 0

# pins.cfg (KEY=VALUE)
$global:Pins = @{}
foreach ($line in Get-Content (Join-Path $PSScriptRoot 'pins.cfg')) {
    if ($line -match '^\s*#' -or $line -notmatch '=') { continue }
    $k, $v = $line -split '=', 2
    $global:Pins[$k.Trim()] = $v.Trim()
}

function Get-EnvOr([string]$Name, [string]$Default) {
    $v = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrEmpty($v)) { return $Default } else { return $v }
}

$global:RomZip        = [IO.Path]::GetFullPath((Get-EnvOr 'ROM_ZIP' (Join-Path $Repo 'input/roms/tektagt.zip')))
$global:RecomponeDir  = [IO.Path]::GetFullPath((Get-EnvOr 'RECOMPONE_DIR' (Join-Path $Repo '../work/recompone')))
$global:RecompOut     = [IO.Path]::GetFullPath((Get-EnvOr 'RECOMP_OUT' (Join-Path $Repo 'generated/recomp')))
$global:HostProject   = Join-Path $Repo 'host/TttHost/TttHost.csproj'
$global:RuntimeProject = Join-Path $Repo 'src/System12.Runtime/System12.Runtime.csproj'
$global:ExtraMsbuild  = @((Get-EnvOr 'EXTRA_MSBUILD' '') -split '\s+' | Where-Object { $_ })

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

function Step([string]$Text) {
    $global:StepNo++
    $global:CurrentStep = $Text
    Write-Host ''
    Write-Host "==> [$ScriptName] step ${StepNo}: $Text" -ForegroundColor Cyan
}

function Ok([string]$Text) { Write-Host "OK [$ScriptName] $Text" -ForegroundColor Green }

function Fail([string]$Reason) { throw $Reason }

# Run the body; on any error print the failing step and exit 1.
function Invoke-Script([scriptblock]$Body) {
    try {
        . $Body
    } catch {
        [Console]::Error.WriteLine('')
        [Console]::Error.WriteLine("FAILED [$ScriptName] step ${StepNo}: $CurrentStep")
        [Console]::Error.WriteLine("  reason: $($_.Exception.Message)")
        exit 1
    }
}

# Run a native command; fail on non-zero exit code (PowerShell does not do that by itself).
function Invoke-Native([string]$Exe, [string[]]$CmdArgs) {
    & $Exe @CmdArgs
    if ($LASTEXITCODE -ne 0) { Fail "command failed (exit $LASTEXITCODE): $Exe $($CmdArgs -join ' ')" }
}

# Run a native command quietly and return only its exit code (never throws).
function Get-NativeExit([string]$Exe, [string[]]$CmdArgs) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Exe @CmdArgs *> $null; return $LASTEXITCODE } finally { $ErrorActionPreference = $old }
}

function Need-Cmd([string]$Name, [string]$Hint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { Fail "'$Name' not found on PATH. $Hint" }
}

function Find-Python {
    $minor = [int]$Pins['PYTHON_MIN_MINOR']
    $candidates = @()
    if ($env:PYTHON) { $candidates += , @($env:PYTHON) }
    $candidates += , @('py', '-3')
    $candidates += , @('python')
    $candidates += , @('python3')
    foreach ($c in $candidates) {
        if (-not (Get-Command $c[0] -ErrorAction SilentlyContinue)) { continue }
        $pre = @($c | Select-Object -Skip 1)
        $rc = Get-NativeExit $c[0] ($pre + @('-c', "import sys; sys.exit(0 if sys.version_info >= (3, $minor) else 1)"))
        if ($rc -eq 0) {
            $global:PythonExe = $c[0]; $global:PythonPre = $pre
            Write-Host ("python: " + (& $c[0] @pre --version 2>&1))
            return
        }
    }
    Fail "no usable Python >= 3.$minor found (tried `$env:PYTHON, py -3, python, python3). Install Python 3."
}

function Invoke-Python([string[]]$PyArgs) { Invoke-Native $PythonExe ($PythonPre + $PyArgs) }

function Test-Dotnet {
    Need-Cmd 'dotnet' "Install the .NET $($Pins['DOTNET_MIN_MAJOR']) SDK (https://dotnet.microsoft.com/download)."
    $global:LASTEXITCODE = 0
    $raw = & dotnet --version
    if ($LASTEXITCODE -ne 0) { Fail "'dotnet --version' failed" }
    $v = ([string]($raw | Select-Object -First 1)).Trim()
    if ($v -notmatch '^(\d+)\.') { Fail "cannot parse dotnet version '$v'" }
    if ([int]$Matches[1] -lt [int]$Pins['DOTNET_MIN_MAJOR']) { Fail "dotnet SDK $v found, need >= $($Pins['DOTNET_MIN_MAJOR']).x" }
    Write-Host "dotnet SDK $v"
}

function Get-Sha256([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

function Get-RecompilerDll { Join-Path $RecomponeDir 'RecompOne.Recompiler/bin/Release/net10.0/recompone.dll' }
