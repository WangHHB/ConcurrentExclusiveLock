[CmdletBinding()]
param(
    [int]$LockInstances = 8,
    [int]$WorkersPerLock = 4,
    [int]$Operations = 1000,
    [string]$PipelineStress = '10s',
    [string]$Seed = '0x6A09E667F3BCC909'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
& .\prepare-vendor.ps1

$cargoCommand = Get-Command cargo -ErrorAction SilentlyContinue
$cargo = if ($cargoCommand) { $cargoCommand.Source } else { Join-Path $HOME '.cargo\bin\cargo.exe' }
if (-not (Test-Path $cargo) -and -not $cargoCommand) {
    throw "Cargo was not found. Install Rust with rustup and reopen PowerShell."
}

& $cargo test --release --workspace --offline
& $cargo run --release --offline -p cel-test-and-benchmark -- `
    --full-semantics `
    --lock-instances $LockInstances `
    --semantic-workers $WorkersPerLock `
    --semantic-operations $Operations `
    --semantic-seed $Seed

& $cargo run --release --offline -p cel-test-and-benchmark -- --pipeline-semantics

& $cargo run --release --offline -p cel-test-and-benchmark -- `
    --pipeline-stress $PipelineStress `
    --lock-instances $LockInstances `
    --semantic-workers $WorkersPerLock `
    --semantic-seed $Seed

& $cargo run --release --offline -p cel-test-and-benchmark -- `
    --contention-stress 30s `
    --semantic-workers 16
