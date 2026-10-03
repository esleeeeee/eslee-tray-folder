<#
.SYNOPSIS
Builds the eslee Tray Folder installer.

.DESCRIPTION
Publishes the app (framework-dependent, win-x64), then compiles the Inno Setup
installer. Output lands in artifacts\installer\ with a SHA-256 hash printed.

Requires: .NET SDK, Inno Setup 6 (ISCC.exe on PATH or in a standard location).
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = "0.1.3"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot "artifacts"
$publishDir = Join-Path $artifacts "publish"

function Find-Iscc {
    $candidates = @(
        (Get-Command "ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }

    if (-not $candidates) {
        throw "ISCC.exe (Inno Setup 6) was not found. Install Inno Setup 6 first."
    }

    return $candidates | Select-Object -First 1
}

$iscc = Find-Iscc
Write-Host "Using ISCC: $iscc"

Write-Host "=== Publishing ==="
if (Test-Path $publishDir) {
    $resolvedPublish = [IO.Path]::GetFullPath($publishDir)
    $boundary = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
    if (-not $resolvedPublish.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish cleanup escaped repository.' }
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}

dotnet publish (Join-Path $repoRoot "src\Eslee.TrayFolder\Eslee.TrayFolder.csproj") `
    -c Release -r win-x64 --self-contained false -p:Version=$Version `
    -o $publishDir --nologo | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

Write-Host "=== Building installer ==="
& $iscc "/DAppVersion=$Version" "/DSourceDir=$publishDir" (Join-Path $PSScriptRoot "eslee-tray-folder.iss") | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed."
}

Write-Host "=== Installer SHA-256 ==="
$installer = Join-Path $artifacts "installer\eslee-tray-folder-setup-v$Version.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Installer was not created: $installer" }
Get-Item -LiteralPath $installer | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    Write-Host "$hash  $($_.Name)"
    Set-Content -Path "$($_.FullName).sha256" -Value "$hash  $($_.Name)" -Encoding ascii
}
