<#
.SYNOPSIS
  Creates the private key that signs Kaze releases, once per release machine.

  The key is written outside the repository (default: ~/.kaze/release-signing-key.pem) and must
  never be committed or shared. Back it up somewhere safe: without it, new releases can't be
  installed by the in-app updater of existing copies.

  The script prints the matching public key. Put it in ReleaseSignature.OfficialPublicKey
  (src/Kaze.Core/Services/ReleaseSignature.cs).
#>
param([string]$Path = (Join-Path $HOME ".kaze/release-signing-key.pem"))

$ErrorActionPreference = "Stop"
if (Test-Path $Path) { throw "A key already exists at $Path. Delete it first only if you really mean to replace it." }
New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
$key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
Set-Content -Path $Path -Value $key.ExportPkcs8PrivateKeyPem() -NoNewline -Encoding ascii
Write-Host "Private key saved to $Path. Back it up and keep it private."
Write-Host "Public key:"
[Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
