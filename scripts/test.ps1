# test: python unittest (tests\) + every dotnet test project found (host\TttHost.Tests, tests\**, src\**\*Tests*.csproj).
# env: EXTRA_MSBUILD (extra msbuild args), PYTHON
$global:ScriptName = 'test.ps1'
. "$PSScriptRoot/common.ps1"
Invoke-Script {
    Set-Location $Repo
    Step 'python unittest (python -m unittest discover -s tests -v)'
    Find-Python
    Invoke-Python @('-m', 'unittest', 'discover', '-s', 'tests', '-v')

    Step "check dotnet >= $($Pins['DOTNET_MIN_MAJOR'])"
    Test-Dotnet

    Step 'locate dotnet test projects'
    $projects = @(foreach ($d in 'host', 'tests', 'src') {
        if (Test-Path $d) { Get-ChildItem $d -Recurse -Filter '*.csproj' | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' -and $_.FullName.Substring($Repo.Length) -match '(?i)test' } }
    }) | Sort-Object FullName -Unique
    if ($projects.Count -eq 0) { Fail 'no dotnet test project found (expected host/TttHost.Tests)' }
    $projects | ForEach-Object { Write-Host "  $($_.FullName)" }

    $empty = @(); $ran = 0
    foreach ($p in $projects) {
        Step "dotnet test $($p.FullName)"
        $outLines = @()
        & dotnet test $p.FullName --nologo @ExtraMsbuild | Tee-Object -Variable outLines | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail "dotnet test failed (exit $LASTEXITCODE): $($p.FullName)" }
        if ($outLines -match 'No test is available') {
            [Console]::Error.WriteLine("WARNING: no tests were discovered in $($p.FullName)")
            $empty += $p.FullName
        } else { $ran++ }
    }
    Write-Host ''
    Write-Host "python unittest: passed; dotnet test projects that ran tests: $ran"
    if ($empty.Count -gt 0) {
        [Console]::Error.WriteLine('WARNING: dotnet test projects with NO tests discovered (nothing was verified there):')
        $empty | ForEach-Object { [Console]::Error.WriteLine("  $_") }
    }
    Write-Host 'test run finished OK'
}
