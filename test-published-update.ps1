param([switch]$Local)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$testExe = Join-Path $PSScriptRoot 'dist\PublishedUpdateTests.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /langversion:5 ('/reference:' + (Join-Path $PSScriptRoot 'dist\LaTaleGarden.exe')) ('/out:' + $testExe) (Join-Path $PSScriptRoot 'Tests\PublishedUpdateTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Published feed test compilation failed.' }
if ($Local) { & $testExe (Join-Path $PSScriptRoot 'updates') --local }
else { & $testExe (Join-Path $PSScriptRoot 'test-results\published-update') }
if ($LASTEXITCODE -ne 0) { throw 'Published feed test failed.' }
