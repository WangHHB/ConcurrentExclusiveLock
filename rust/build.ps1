[CmdletBinding()]
param(
    [switch]$SkipClippy,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
& .\prepare-vendor.ps1

function Find-Cargo {
    $command = Get-Command cargo -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidate = Join-Path $HOME '.cargo\bin\cargo.exe'
    if (Test-Path $candidate) { return $candidate }

    throw "Cargo was not found. Install Rust with rustup, then reopen PowerShell. Expected cargo in PATH or $candidate"
}

$cargo = Find-Cargo
Write-Host "Cargo: $cargo"
& $cargo --version
& $cargo fmt --all
& $cargo fmt --all -- --check

if (-not $SkipClippy) {
    & $cargo clippy --workspace --all-targets --offline --no-deps -- -D warnings
}

& $cargo build --release --workspace --offline

if (-not $SkipTests) {
    & $cargo test --release --workspace --offline
}

New-Item -ItemType Directory -Force -Path 'Artifacts\windows-x64' | Out-Null
$exe = 'target\release\cel-test-and-benchmark.exe'
if (Test-Path $exe) {
    Copy-Item $exe 'Artifacts\windows-x64\cel-test-and-benchmark.exe' -Force
}
Write-Host "Build completed."
Write-Host "Benchmark executable: Artifacts\windows-x64\cel-test-and-benchmark.exe"
