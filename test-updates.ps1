param([string]$ResultDirectory = (Join-Path $PSScriptRoot 'test-results\updates'))
$ErrorActionPreference = 'Stop'
$ResultDirectory = [IO.Path]::GetFullPath($ResultDirectory)
New-Item -ItemType Directory -Force -Path $ResultDirectory | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$refs = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
foreach ($kind in @('healthy','failed')) {
    $flags = @()
    if ($kind -eq 'failed') { $flags += '/define:FAIL_STARTUP' }
    & $compiler /nologo /target:winexe /platform:x64 /langversion:5 ('/out:' + (Join-Path $ResultDirectory ($kind + '.exe'))) @refs @flags (Join-Path $PSScriptRoot 'Tests\UpdateFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
}
$testExe = Join-Path $PSScriptRoot 'dist\UpdateTests.exe'
& $compiler /nologo /target:exe /platform:x64 /langversion:5 ('/out:' + $testExe) @refs ('/reference:' + (Join-Path $PSScriptRoot 'dist\LaTaleGarden.exe')) (Join-Path $PSScriptRoot 'Tests\UpdateTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Update test compilation failed.' }
& $testExe $ResultDirectory (Join-Path $ResultDirectory 'healthy.exe')
if ($LASTEXITCODE -ne 0) { throw 'Update tests failed.' }
