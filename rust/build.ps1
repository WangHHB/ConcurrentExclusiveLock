[CmdletBinding()]
param(
    [switch]$SkipClippy,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

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
    & $cargo clippy --workspace --all-targets -- -D warnings
}

& $cargo build --release --workspace

if (-not $SkipTests) {
    & $cargo test --release --workspace
}

Write-Host "Build completed."
Write-Host "Benchmark executable: target\release\cel-test-and-benchmark.exe"
