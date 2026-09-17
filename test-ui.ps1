param([string]$ResultDirectory = (Join-Path $PSScriptRoot 'test-results\ui'))
$ErrorActionPreference = 'Stop'
$ResultDirectory = [IO.Path]::GetFullPath($ResultDirectory)
New-Item -ItemType Directory -Force -Path $ResultDirectory | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @('System.dll','System.Core.dll','System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$refs += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework ('WPF\' + $_)) }
$refs += '/reference:' + (Join-Path $PSScriptRoot 'dist\LaTaleGarden.exe')
$testExe = Join-Path $PSScriptRoot 'dist\RegionUiTests.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /langversion:5 ('/out:' + $testExe) @refs (Join-Path $PSScriptRoot 'Tests\RegionUiTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI test compilation failed.' }
& $testExe $ResultDirectory
if ($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
