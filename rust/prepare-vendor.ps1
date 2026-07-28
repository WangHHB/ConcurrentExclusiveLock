[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$marker = Join-Path $PSScriptRoot 'vendor\parking_lot\Cargo.toml'
$archive = Join-Path $PSScriptRoot 'parking_lot-vendor.zip'

if (Test-Path $marker) {
    return
}

if (-not (Test-Path $archive)) {
    throw "Missing $archive"
}

$vendor = Join-Path $PSScriptRoot 'vendor'
if (Test-Path $vendor) {
    Remove-Item $vendor -Recurse -Force
}

Expand-Archive -Path $archive -DestinationPath $PSScriptRoot -Force

if (-not (Test-Path $marker)) {
    throw "Vendor extraction failed: $marker was not created."
}
