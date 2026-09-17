param([string]$ResultDirectory = (Join-Path $PSScriptRoot 'test-results'))
$ErrorActionPreference = 'Stop'
$ResultDirectory = [IO.Path]::GetFullPath($ResultDirectory)
New-Item -ItemType Directory -Force -Path $ResultDirectory | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$refs = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xaml.dll','System.Windows.Forms.dll','System.Drawing.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$refs += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework ('WPF\' + $_)) }
$sources = @('Models.cs','RegionManagement.cs','LauncherRegion.cs','LaunchEngine.cs','WindowsPlatform.cs','Program.cs','LauncherWindow.cs','UpdateService.cs','UpdateInstaller.cs','LauncherUpdates.cs','Tests\EngineTests.cs','Tests\RegionTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$testExe = Join-Path $ResultDirectory 'EngineTests.exe'
& $compiler /nologo /target:exe /platform:x64 /langversion:5 /main:EngineTests ('/out:' + $testExe) @refs @sources
if ($LASTEXITCODE -ne 0) { throw '测试编译失败。' }
& $testExe $ResultDirectory
if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
