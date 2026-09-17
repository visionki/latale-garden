param(
    [string]$ResultDirectory = (Join-Path $PSScriptRoot 'test-results\update-install'),
    [string]$TargetRoot
)
$ErrorActionPreference = 'Stop'
$ResultDirectory = [IO.Path]::GetFullPath($ResultDirectory)
New-Item -ItemType Directory -Force -Path $ResultDirectory | Out-Null
if (-not $TargetRoot) { $TargetRoot = Join-Path $ResultDirectory ([Guid]::NewGuid().ToString('N')) }
$TargetRoot = [IO.Path]::GetFullPath($TargetRoot)
if ((Test-Path -LiteralPath $TargetRoot) -and @(Get-ChildItem -LiteralPath $TargetRoot -Force).Count -ne 0) { throw 'TargetRoot must be new or empty.' }
New-Item -ItemType Directory -Force -Path $TargetRoot | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$refs = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xaml.dll','System.Windows.Forms.dll','System.Drawing.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$refs += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework ('WPF\' + $_)) }
$utf8 = New-Object Text.UTF8Encoding($false)
$testProgram = Join-Path $ResultDirectory 'FixtureProgram.cs'
$testAssembly = Join-Path $ResultDirectory 'FixtureAssembly.cs'
[IO.File]::WriteAllText($testProgram, ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Program.cs')) -replace 'public const string Version = "[^"]+";', 'public const string Version = "9.0.0";'), $utf8)
[IO.File]::WriteAllText($testAssembly, ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'AssemblyInfo.cs')) -replace '(Assembly(?:File)?Version)\("[^"]+"\)', '$1("9.0.0.0")'), $utf8)
$sources = @('Models.cs','RegionManagement.cs','LauncherRegion.cs','LaunchEngine.cs','WindowsPlatform.cs','LauncherWindow.cs','UpdateService.cs','UpdateInstaller.cs','LauncherUpdates.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$sources += @($testProgram, $testAssembly)
$resources = @(
    ('/resource:' + (Join-Path $PSScriptRoot 'MainWindow.xaml') + ',MainWindow.xaml'),
    ('/resource:' + (Join-Path $PSScriptRoot 'Assets\hero.png') + ',hero.png'),
    ('/resource:' + (Join-Path $PSScriptRoot 'Assets\launch-button.png') + ',launch-button.png'),
    ('/resource:' + (Join-Path $PSScriptRoot 'Assets\app.ico') + ',app.ico'),
    ('/resource:' + (Join-Path $PSScriptRoot 'Assets\app-icon.png') + ',app-icon.png'),
    ('/resource:' + (Join-Path $PSScriptRoot 'Assets\update-public-key.xml') + ',update-public-key.xml')
)
$healthy = Join-Path $ResultDirectory 'healthy-ui.exe'
$failed = Join-Path $ResultDirectory 'failed.exe'
& $compiler /nologo /target:winexe /platform:x64 /langversion:5 /main:LaTaleGarden.Program ('/out:' + $healthy) @refs @resources @sources
if ($LASTEXITCODE -ne 0) { throw 'Real UI fixture compilation failed.' }
& $compiler /nologo /target:winexe /platform:x64 /langversion:5 /define:FAIL_STARTUP ('/out:' + $failed) @refs (Join-Path $PSScriptRoot 'Tests\UpdateFixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Failure fixture compilation failed.' }
foreach ($kind in @('healthy-ui','failed')) {
    $file = Join-Path $ResultDirectory ($kind + '.exe')
    $manifest = [ordered]@{ schema = 1; releases = @([ordered]@{
        version = '9.0.0'; channel = 'preview'; notes = 'LOCAL INSTALLER TEST ONLY'
        url = 'https://github.com/visionki/latale-garden/releases/download/v9.0.0/LaTaleGarden.exe'
        sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        size = (Get-Item -LiteralPath $file).Length; minimumWindowsBuild = 10240; minimumFrameworkRelease = 528040
    }) }
    $inputFile = Join-Path $ResultDirectory ($kind + '-input.json')
    [IO.File]::WriteAllText($inputFile, ($manifest | ConvertTo-Json -Depth 5), $utf8)
    & (Join-Path $PSScriptRoot 'sign-document.ps1') -InputFile $inputFile -OutputFile (Join-Path $ResultDirectory ($kind + '-feed.json'))
}
$testExe = Join-Path $PSScriptRoot 'dist\UpdateInstallTests.exe'
& $compiler /nologo /target:exe /platform:x64 /langversion:5 ('/out:' + $testExe) @refs ('/reference:' + (Join-Path $PSScriptRoot 'dist\LaTaleGarden.exe')) (Join-Path $PSScriptRoot 'Tests\UpdateInstallTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Installer test compilation failed.' }
& $testExe $TargetRoot (Join-Path $PSScriptRoot 'dist\LaTaleGarden.exe') $healthy $failed (Join-Path $ResultDirectory 'healthy-ui-feed.json') (Join-Path $ResultDirectory 'failed-feed.json')
if ($LASTEXITCODE -ne 0) { throw 'Installer tests failed.' }
Write-Output ('Evidence: ' + $TargetRoot)
