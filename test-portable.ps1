param(
    [string]$Executable = (Join-Path $PSScriptRoot 'dist\LaTaleGarden.exe'),
    [string]$ResultDirectory = (Join-Path $PSScriptRoot 'test-results\portable')
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$ResultDirectory = [IO.Path]::GetFullPath($ResultDirectory)
$runRoot = Join-Path $ResultDirectory ([Guid]::NewGuid().ToString('N'))
$locationA = Join-Path $runRoot 'location A'
$locationB = Join-Path $runRoot 'location B'
$workingDirectory = Join-Path $runRoot 'different working directory'
@($locationA, $locationB, $workingDirectory) | ForEach-Object { New-Item -ItemType Directory -Path $_ -Force | Out-Null }
$preferences = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'LaTaleGarden\settings.json'
$originalSettings = if (Test-Path -LiteralPath $preferences) { (Get-FileHash -LiteralPath $preferences -Algorithm SHA256).Hash } else { $null }

function Render-Launcher([string]$path, [string]$preview) {
    $child = Start-Process -FilePath $path -ArgumentList @('--render-preview', ('"' + $preview + '"')) -WorkingDirectory $workingDirectory -WindowStyle Hidden -PassThru
    try {
        if (-not $child.WaitForExit(30000)) { $child.Kill(); throw 'Standalone launcher rendering timed out.' }
        if ($child.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $preview)) { throw 'Standalone launcher failed to render.' }
    } finally { $child.Dispose() }
}

$copyA = Join-Path $locationA 'LaTaleGarden.exe'
$copyB = Join-Path $locationB 'LaTaleGarden.exe'
Copy-Item -LiteralPath $Executable -Destination $copyA
Render-Launcher $copyA (Join-Path $runRoot 'location-a.png')
if (@(Get-ChildItem -LiteralPath $locationA -Force).Count -ne 1) { throw 'Launcher created adjacent files.' }
Write-Output 'PASS standalone EXE renders with no adjacent configuration or image files'

# Both fully resolved paths are inside the newly created, task-specific run directory.
$allowedRoot = [IO.Path]::GetFullPath($runRoot).TrimEnd('\') + '\'
foreach ($path in @($copyA, $copyB)) {
    if (-not ([IO.Path]::GetFullPath($path).StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase))) { throw 'Move outside portable test directory rejected.' }
}
Move-Item -LiteralPath $copyA -Destination $copyB
Render-Launcher $copyB (Join-Path $runRoot 'location-b.png')
if ((Get-FileHash -LiteralPath (Join-Path $runRoot 'location-a.png')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $runRoot 'location-b.png')).Hash) { throw 'Moving the launcher changed its rendered settings or embedded assets.' }
Write-Output 'PASS moving only the EXE preserves the rendered UI and saved game directory'
if (@(Get-ChildItem -LiteralPath $locationA -Force).Count -ne 0 -or @(Get-ChildItem -LiteralPath $locationB -Force).Count -ne 1 -or @(Get-ChildItem -LiteralPath $workingDirectory -Force).Count -ne 0) { throw 'Executable or working directories contain unexpected files.' }
Write-Output 'PASS both executable locations and the separate working directory remain clean'
$currentSettings = if (Test-Path -LiteralPath $preferences) { (Get-FileHash -LiteralPath $preferences -Algorithm SHA256).Hash } else { $null }
if ($originalSettings -ne $currentSettings) { throw 'Rendering changed the user settings file.' }
Write-Output 'PASS original user settings are unchanged'
Write-Output ('Evidence: ' + $runRoot)
