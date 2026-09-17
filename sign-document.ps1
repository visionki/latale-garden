param(
    [string]$InputFile,
    [string]$OutputFile,
    [switch]$InitializeKey,
    [string]$KeyFile = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'LaTaleGardenPublisher\update-key.dpapi')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$publicFile = Join-Path $PSScriptRoot 'Assets\update-public-key.xml'
$utf8 = New-Object Text.UTF8Encoding($false)
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider(3072)
$rsa.PersistKeyInCsp = $false
try {
    if ($InitializeKey) {
        if ((Test-Path -LiteralPath $KeyFile) -or (Test-Path -LiteralPath $publicFile)) { throw 'Key already exists; refusing to rotate an installed update trust key.' }
        New-Item -ItemType Directory -Force -Path (Split-Path $KeyFile -Parent) | Out-Null
        $privateBytes = $utf8.GetBytes($rsa.ToXmlString($true))
        try {
            $protected = [Security.Cryptography.ProtectedData]::Protect($privateBytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
            [IO.File]::WriteAllBytes($KeyFile, $protected)
        } finally { [Array]::Clear($privateBytes, 0, $privateBytes.Length) }
        [IO.File]::WriteAllText($publicFile, $rsa.ToXmlString($false), $utf8)
        Write-Output 'Update signing key initialized. Only the public key belongs in the repository.'
        return
    }
    if (-not $InputFile -or -not $OutputFile) { throw 'InputFile and OutputFile are required.' }
    if (-not (Test-Path -LiteralPath $KeyFile)) { throw 'Publisher signing key not found. Do not replace the public key used by existing clients.' }
    $privateBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($KeyFile), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try { $rsa.FromXmlString($utf8.GetString($privateBytes)) } finally { [Array]::Clear($privateBytes, 0, $privateBytes.Length) }
    if ($rsa.ToXmlString($false) -ne [IO.File]::ReadAllText($publicFile)) { throw 'Private key does not match the client public key.' }
    $payload = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $InputFile).Path)
    if ($payload.Length -gt 120000) { throw 'Document too large.' }
    $null = $utf8.GetString($payload) | ConvertFrom-Json
    $signature = $rsa.SignData($payload, [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
    $envelope = [ordered]@{ payload = [Convert]::ToBase64String($payload); signature = [Convert]::ToBase64String($signature) }
    $destination = [IO.Path]::GetFullPath($OutputFile)
    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    [IO.File]::WriteAllText($destination, ($envelope | ConvertTo-Json), $utf8)
    Write-Output ('Signed: ' + $destination)
} finally { $rsa.Dispose() }
