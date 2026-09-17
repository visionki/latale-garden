param(
    [switch]$AllowSystemLocaleChanges,
    [string]$ResultDirectory = (Join-Path $PSScriptRoot 'test-results\region-live')
)
$ErrorActionPreference = 'Stop'
if (-not $AllowSystemLocaleChanges) { throw 'This opt-in test changes the SYSTEM locale and restores it. Close the game first, then pass -AllowSystemLocaleChanges to authorize the test.' }
$testExe = Join-Path $PSScriptRoot 'dist\RegionLiveTests.exe'
$launcher = Join-Path $PSScriptRoot 'dist\LaTaleGarden.exe'
if (-not (Test-Path -LiteralPath $launcher)) { throw 'Run build.ps1 first.' }
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /langversion:5 ('/reference:' + $launcher) ('/out:' + $testExe) (Join-Path $PSScriptRoot 'Tests\RegionLiveTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Live-test compilation failed.' }
$ResultDirectory = [IO.Path]::GetFullPath($ResultDirectory)
$runRoot = Join-Path $ResultDirectory ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$child = Start-Process -FilePath $testExe -Verb RunAs -WindowStyle Hidden -ArgumentList @('--allow-system-locale-changes', ('"' + $runRoot + '"')) -PassThru
Write-Output ('Live test PID: ' + $child.Id)
Write-Output ('Evidence: ' + $runRoot)
# The test writes complete.txt asynchronously; its independent guard preserves the baseline on failure.
$child.Dispose()
