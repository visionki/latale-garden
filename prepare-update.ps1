param(
    [string]$Executable = (Join-Path $PSScriptRoot 'dist\LaTaleGarden.exe'),
    [ValidateSet('preview','stable')][string]$Channel = 'stable',
    [Parameter(Mandatory = $true)][string]$NotesFile,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'updates')
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Executable)
$version = '{0}.{1}.{2}' -f $versionInfo.FileMajorPart, $versionInfo.FileMinorPart, $versionInfo.FileBuildPart
if ($versionInfo.ProductName -ne 'LaTale Garden') { throw 'Expected the built LaTale Garden executable.' }
$notes = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesFile).Path).Trim()
if ($notes.Length -gt 8000) { throw 'Release notes are too long for the updater.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$inputFile = Join-Path $OutputDirectory 'updates.source.json'
$previous = @()
if (Test-Path -LiteralPath $inputFile) { $previous = @((Get-Content -LiteralPath $inputFile -Raw | ConvertFrom-Json).releases | Where-Object { $_.channel -ne $Channel -and $_.version -ne $version }) }
$release = [ordered]@{
    version = $version; channel = $Channel; notes = ($notes -replace '(?m)^- ', '• ')
    url = 'https://github.com/visionki/latale-garden/releases/download/v' + $version + '/LaTaleGarden.exe'
    sha256 = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash.ToLowerInvariant()
    size = (Get-Item -LiteralPath $Executable).Length
    minimumWindowsBuild = 10240; minimumFrameworkRelease = 528040
}
$manifest = [ordered]@{ schema = 1; releases = @($previous) + @($release) }
[IO.File]::WriteAllText($inputFile, ($manifest | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
& (Join-Path $PSScriptRoot 'sign-document.ps1') -InputFile $inputFile -OutputFile (Join-Path $OutputDirectory 'updates.json')
Write-Output ('Prepared v' + $version + '. Publish the Release asset before publishing this feed.')
