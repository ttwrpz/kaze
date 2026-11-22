<#
.SYNOPSIS
  Builds a Kaze release: setup program, portable exe, zip and winget manifests.

.EXAMPLE
  ./build/publish.ps1 -Version 1.1.0

  Output goes to artifacts/:
    Kaze-Setup-<version>.exe       per-user installer (attach to the GitHub release)
    Kaze-Setup-<version>.exe.sig   its signature (attach too, the in-app updater requires it)
    Kaze-<version>-portable.exe    the whole app in one file, nothing to install
    Kaze-<version>-win-x64.zip     the same app as a folder, unzip and run
    winget/manifests/...           manifests for winget-pkgs, with the installer SHA256

  Signing needs the private key from build/new-signing-key.ps1 (default ~/.kaze/release-signing-key.pem).
#>
param(
    [string]$Version,
    [string]$SigningKey = $(if ($env:KAZE_SIGNING_KEY) { $env:KAZE_SIGNING_KEY } else { Join-Path $HOME ".kaze/release-signing-key.pem" }),
    [string]$Repository = "ttwrpz/kaze",
    [string]$Runtime = "win-x64",
    [string]$License = "MIT",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$appProj = Join-Path $root "src/Kaze.App/Kaze.App.csproj"
$setupDir = Join-Path $root "tools/Kaze.Setup"

if (-not $Version) {
    $Version = ([xml](Get-Content $appProj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "Kaze $Version ($Runtime)" -ForegroundColor Cyan

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item -ItemType Directory -Force $artifacts | Out-Null

if (-not $SkipTests) {
    dotnet test --project (Join-Path $root "tests/Kaze.Core.Tests")
    if ($LASTEXITCODE) { throw "Tests failed" }
}

$appOut = Join-Path $artifacts "app"
dotnet publish $appProj -c Release -r $Runtime -p:Platform=x64 -p:Version=$Version -o $appOut
if ($LASTEXITCODE) { throw "App publish failed" }
Get-ChildItem $appOut -Filter *.pdb -Recurse | Remove-Item

# The portable exe unpacks itself to %TEMP%\.net\Kaze on first start.
$portableOut = Join-Path $artifacts "portable"
dotnet publish $appProj -c Release -r $Runtime -p:Platform=x64 -p:Version=$Version -o $portableOut `
    -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true
if ($LASTEXITCODE) { throw "Portable publish failed" }
Move-Item (Join-Path $portableOut "Kaze.exe") (Join-Path $artifacts "Kaze-$Version-portable.exe")
Remove-Item $portableOut -Recurse -Force

# The zip doubles as the setup payload.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $artifacts "Kaze-$Version-$Runtime.zip"
[System.IO.Compression.ZipFile]::CreateFromDirectory($appOut, $zip, [System.IO.Compression.CompressionLevel]::SmallestSize, $false)
$payload = Join-Path $setupDir "payload.zip"
Copy-Item $zip $payload -Force

try {
    $setupOut = Join-Path $artifacts "setup"
    dotnet publish (Join-Path $setupDir "Kaze.Setup.csproj") -c Release -p:Version=$Version -o $setupOut
    if ($LASTEXITCODE) { throw "Setup publish failed" }
}
finally {
    Remove-Item $payload -Force -ErrorAction SilentlyContinue
}
$setup = Join-Path $artifacts "Kaze-Setup-$Version.exe"
Move-Item (Join-Path $setupOut "Kaze-Setup.exe") $setup
Remove-Item $setupOut -Recurse -Force
Remove-Item $appOut -Recurse -Force

if (-not (Test-Path $SigningKey)) { throw "No signing key at $SigningKey. Run build/new-signing-key.ps1 once, or pass -SigningKey." }
$key = [System.Security.Cryptography.ECDsa]::Create()
$key.ImportFromPem((Get-Content $SigningKey -Raw))
# Signed statement, as in ReleaseSignature.Statement: "kaze-release", the version and the setup's SHA-256.
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$statement = [System.Text.Encoding]::UTF8.GetBytes("kaze-release`n$(([version]$Version).ToString(3))`n$hash`n")
try { $signature = [Convert]::ToBase64String($key.SignData($statement, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) }
finally { $key.Dispose() }
Set-Content -Path "$setup.sig" -Value $signature -NoNewline -Encoding ascii

# winget-pkgs does not accept singleton manifests, so write the three-file form.
$sha = (Get-FileHash $setup -Algorithm SHA256).Hash
$id = "ttwrpz.Kaze"
$url = "https://github.com/$Repository/releases/download/v$Version/Kaze-Setup-$Version.exe"
$manifestDir = Join-Path $artifacts "winget/manifests/t/ttwrpz/Kaze/$Version"
New-Item -ItemType Directory -Force $manifestDir | Out-Null
$schema = "1.9.0"

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@ | Set-Content (Join-Path $manifestDir "$id.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: exe
Scope: user
InstallModes:
- interactive
- silent
InstallerSwitches:
  Silent: /silent
  SilentWithProgress: /silent
  Upgrade: /silent
UpgradeBehavior: install
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: $schema
"@ | Set-Content (Join-Path $manifestDir "$id.installer.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: ttwrpz
PublisherUrl: https://github.com/ttwrpz
PackageName: Kaze for airCloud Go
PackageUrl: https://github.com/$Repository
Moniker: kaze
License: $License
LicenseUrl: https://github.com/$Repository/blob/main/LICENSE
ShortDescription: Unofficial Windows tray app for Hitachi air conditioners that use airCloud Go. Not affiliated with Hitachi.
Tags:
- air-conditioner
- hitachi
- tray
ManifestType: defaultLocale
ManifestVersion: $schema
"@ | Set-Content (Join-Path $manifestDir "$id.locale.en-US.yaml") -Encoding utf8

Write-Host ""
Get-ChildItem $artifacts -File | ForEach-Object { "{0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
Write-Host "Setup SHA256 $sha"
Write-Host "winget manifests: $manifestDir"
