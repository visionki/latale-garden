param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows x64 与 .NET Framework 4.8。' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$refs = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xaml.dll','System.Windows.Forms.dll','System.Drawing.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$refs += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework ('WPF\' + $_)) }
$sourceFiles = @('Models.cs','LaunchEngine.cs','WindowsPlatform.cs','Program.cs','LauncherWindow.cs','AssemblyInfo.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$executable = Join-Path $OutputDirectory 'LaTaleGarden.exe'
$options = @('/nologo','/target:winexe','/platform:x64','/optimize+','/langversion:5','/main:LaTaleGarden.Program',('/out:' + $executable),('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),('/resource:' + (Join-Path $PSScriptRoot 'MainWindow.xaml') + ',MainWindow.xaml'),('/resource:' + (Join-Path $PSScriptRoot 'Assets\hero.png') + ',hero.png'),('/resource:' + (Join-Path $PSScriptRoot 'Assets\launch-button.png') + ',launch-button.png'))
& $compiler @options @refs @sourceFiles
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
# Remove only the obsolete sidecar emitted by earlier versions of this build script.
if (Test-Path -LiteralPath ($executable + '.config')) { Remove-Item -LiteralPath ($executable + '.config') -Force }
Write-Output $executable
